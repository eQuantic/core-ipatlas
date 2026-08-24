namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Turns parsed country and ASN ranges into one .eqatlas file. The two layers
/// rarely share boundaries, so a sweep walks every cut point and emits the
/// segments where either layer knows something, merging neighbours that agree
/// — the output is sorted, non-overlapping, and as small as the data allows.
/// </summary>
public sealed class DatasetBuilder
{
    private readonly List<CountryRange> _countries = [];
    private readonly List<AsnRange> _asns = [];

    /// <summary>Adds country delegations (RIR data).</summary>
    public DatasetBuilder AddCountries(IEnumerable<CountryRange> ranges)
    {
        _countries.AddRange(ranges);
        return this;
    }

    /// <summary>Adds routed-ASN ranges (optional enrichment).</summary>
    public DatasetBuilder AddAsns(IEnumerable<AsnRange> ranges)
    {
        _asns.AddRange(ranges);
        return this;
    }

    /// <summary>Combines both layers and writes the .eqatlas file.</summary>
    public void Write(Stream output, string source, DateTimeOffset builtAt)
    {
        var v4 = Combine(
            _countries.Where(c => !c.IsV6).Select(c => (c.Start, c.End, Payload: (AtlasFormat.PackCountry(c.CountryCode), 0u))),
            _asns.Where(a => !a.IsV6).Select(a => (a.Start, a.End, a.Asn)));
        var v6 = Combine(
            _countries.Where(c => c.IsV6).Select(c => (c.Start, c.End, Payload: (AtlasFormat.PackCountry(c.CountryCode), 0u))),
            _asns.Where(a => a.IsV6).Select(a => (a.Start, a.End, a.Asn)));

        AtlasFormat.WriteHeader(output, builtAt, source, v4.Count, v6.Count);
        foreach (var segment in v4)
        {
            AtlasFormat.WriteV4Record(output, (uint)segment.Start, (uint)segment.End, segment.Country, segment.Asn);
        }

        foreach (var segment in v6)
        {
            AtlasFormat.WriteV6Record(output, segment.Start, segment.End, segment.Country, segment.Asn);
        }
    }

    private readonly record struct Segment(UInt128 Start, UInt128 End, ushort Country, uint Asn);

    private static List<Segment> Combine(
        IEnumerable<(UInt128 Start, UInt128 End, (ushort Country, uint) Payload)> countryRanges,
        IEnumerable<(UInt128 Start, UInt128 End, uint Asn)> asnRanges)
    {
        var countries = Normalize(countryRanges.Select(r => (r.Start, r.End, Value: (uint)r.Payload.Item1)));
        var asns = Normalize(asnRanges.Select(r => (r.Start, r.End, Value: r.Asn)));

        // Sweep over every boundary of both layers.
        var cuts = new SortedSet<UInt128>();
        foreach (var (start, end, _) in countries)
        {
            cuts.Add(start);
            if (end != UInt128.MaxValue)
            {
                cuts.Add(end + 1);
            }
        }

        foreach (var (start, end, _) in asns)
        {
            cuts.Add(start);
            if (end != UInt128.MaxValue)
            {
                cuts.Add(end + 1);
            }
        }

        var points = cuts.ToArray();
        var result = new List<Segment>();
        int countryIndex = 0, asnIndex = 0;

        for (var i = 0; i < points.Length; i++)
        {
            var start = points[i];
            var end = i + 1 < points.Length ? points[i + 1] - 1 : UInt128.MaxValue;

            var country = ValueAt(countries, ref countryIndex, start);
            var asn = ValueAt(asns, ref asnIndex, start);
            if (country == 0 && asn == 0)
            {
                continue;
            }

            // Merge with the previous segment when contiguous and identical.
            if (result.Count > 0
                && result[^1] is { } last
                && last.End + 1 == start
                && last.Country == country
                && last.Asn == asn)
            {
                result[^1] = last with { End = end };
            }
            else
            {
                result.Add(new Segment(start, end, (ushort)country, asn));
            }
        }

        return result;
    }

    /// <summary>Sorted, overlap-resolved (first wins) copy of one layer.</summary>
    private static List<(UInt128 Start, UInt128 End, uint Value)> Normalize(
        IEnumerable<(UInt128 Start, UInt128 End, uint Value)> ranges)
    {
        var sorted = ranges.Where(r => r.Start <= r.End).OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
        var result = new List<(UInt128 Start, UInt128 End, uint Value)>(sorted.Count);
        foreach (var range in sorted)
        {
            if (result.Count > 0 && range.Start <= result[^1].End)
            {
                // Overlap: honor the earlier delegation, keep any tail.
                if (range.End <= result[^1].End)
                {
                    continue;
                }

                result.Add((result[^1].End + 1, range.End, range.Value));
            }
            else
            {
                result.Add(range);
            }
        }

        return result;
    }

    /// <summary>The layer's value covering a point, advancing the cursor monotonically.</summary>
    private static uint ValueAt(List<(UInt128 Start, UInt128 End, uint Value)> layer, ref int index, UInt128 point)
    {
        while (index < layer.Count && layer[index].End < point)
        {
            index++;
        }

        return index < layer.Count && layer[index].Start <= point ? layer[index].Value : 0;
    }
}
