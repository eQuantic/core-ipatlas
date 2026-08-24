using System.Text;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>What a build produced, so the compiler can report it instead of just claiming success.</summary>
/// <param name="V4Ranges">IPv4 segments written.</param>
/// <param name="V6Ranges">IPv6 segments written.</param>
/// <param name="Locations">Distinct places written.</param>
/// <param name="Bytes">Size of the finished dataset.</param>
/// <param name="CountryFromRegistry">Segments whose country came from a registry delegation.</param>
/// <param name="CountryFromGeofeed">Segments whose country came from an operator's geofeed.</param>
/// <param name="CountryFromCloud">Segments whose country came from a cloud provider's own ranges.</param>
public readonly record struct BuildReport(
    int V4Ranges, int V6Ranges, int Locations, long Bytes,
    int CountryFromRegistry, int CountryFromGeofeed, int CountryFromCloud);

/// <summary>
/// Turns parsed ranges from every source into one .eqatlas file.
/// <para>
/// Sources are layers with a precedence, not one pile. The layers rarely share
/// boundaries, so a sweep walks every cut point across all of them and, for
/// each resulting segment, takes each field from the highest-ranked layer that
/// has one: a cloud provider's own region beats an operator's geofeed beats a
/// registry's delegation. Flags accumulate across every layer, because
/// "datacenter" and "anycast" are true no matter who noticed. Neighbouring
/// segments that agree are merged, so the output is sorted, non-overlapping,
/// and as small as the data allows.
/// </para>
/// </summary>
public sealed class DatasetBuilder
{
    private const int RegistryPrecedence = 10;
    private const int AsnPrecedence = 20;
    private const int GeofeedPrecedence = 30;
    private const int CloudPrecedence = 40;
    private const int OverridePrecedence = 50;

    private sealed record Layer(int Precedence, LocationSource Source, List<AtlasEntry> Entries);

    private readonly List<Layer> _layers = [];

    /// <summary>Adds registry delegations: the base layer every other source may correct.</summary>
    public DatasetBuilder AddRegistry(IEnumerable<AtlasEntry> entries) =>
        Add(RegistryPrecedence, LocationSource.RegistryDelegation, entries);

    /// <summary>Adds routed-ASN ranges, which contribute the AS number and any flags.</summary>
    public DatasetBuilder AddAsns(IEnumerable<AtlasEntry> entries) =>
        Add(AsnPrecedence, LocationSource.None, entries);

    /// <summary>Adds an operator's self-published geofeed (RFC 8805).</summary>
    public DatasetBuilder AddGeofeed(IEnumerable<AtlasEntry> entries) =>
        Add(GeofeedPrecedence, LocationSource.Geofeed, entries);

    /// <summary>Adds a cloud provider's published ranges.</summary>
    public DatasetBuilder AddCloud(IEnumerable<AtlasEntry> entries) =>
        Add(CloudPrecedence, LocationSource.CloudProvider, entries);

    /// <summary>Adds local corrections, which outrank every published source.</summary>
    public DatasetBuilder AddOverrides(IEnumerable<AtlasEntry> entries) =>
        Add(OverridePrecedence, LocationSource.Override, entries);

    private DatasetBuilder Add(int precedence, LocationSource source, IEnumerable<AtlasEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _layers.Add(new Layer(precedence, source, entries.ToList()));
        return this;
    }

    /// <summary>Combines every layer and writes the .eqatlas file.</summary>
    public BuildReport Write(Stream output, string source, DateTimeOffset builtAt)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);

        var places = new PlaceTable();
        var v4 = Combine(isV6: false, places);
        var v6 = Combine(isV6: true, places);

        // The runtime's writer owns the layout, so there is one implementation of
        // the format on the write side and it is the one consumers can also use.
        var writer = new AtlasWriter(source, builtAt);
        foreach (var segment in v4)
        {
            writer.AddV4((uint)segment.Start, (uint)segment.End, places.Describe(segment));
        }

        foreach (var segment in v6)
        {
            writer.AddV6(segment.Start, segment.End, places.Describe(segment));
        }

        var counted = new CountingStream(output);
        writer.WriteTo(counted);

        return new BuildReport(
            v4.Count, v6.Count, places.Count, counted.Written,
            Provenance(v4, v6, LocationSource.RegistryDelegation),
            Provenance(v4, v6, LocationSource.Geofeed),
            Provenance(v4, v6, LocationSource.CloudProvider));
    }

    /// <summary>Counts what went past, so the report can say how big the file is.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;

        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Written += buffer.Length;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static int Provenance(List<Segment> v4, List<Segment> v6, LocationSource source)
    {
        var wanted = (ushort)((byte)source << 8);
        var count = 0;
        foreach (var segment in v4)
        {
            if ((segment.Traits & 0xFF00) == wanted && segment.Country != 0)
            {
                count++;
            }
        }

        foreach (var segment in v6)
        {
            if ((segment.Traits & 0xFF00) == wanted && segment.Country != 0)
            {
                count++;
            }
        }

        return count;
    }

    private readonly record struct Segment(UInt128 Start, UInt128 End, ushort Country, uint Asn, ushort Traits, uint Location);

    private sealed record Normalized(int Precedence, LocationSource Source, List<AtlasEntry> Entries)
    {
        public int Cursor { get; set; }

        public AtlasEntry? At(UInt128 point)
        {
            while (Cursor < Entries.Count && Entries[Cursor].End < point)
            {
                Cursor++;
            }

            return Cursor < Entries.Count && Entries[Cursor].Start <= point ? Entries[Cursor] : null;
        }
    }

    private List<Segment> Combine(bool isV6, PlaceTable places)
    {
        var layers = _layers
            .Select(layer => new Normalized(
                layer.Precedence,
                layer.Source,
                Normalize(layer.Entries.Where(entry => entry.IsV6 == isV6))))
            .Where(layer => layer.Entries.Count > 0)
            .OrderByDescending(layer => layer.Precedence)
            .ToList();

        if (layers.Count == 0)
        {
            return [];
        }

        var points = CutPoints(layers);
        var result = new List<Segment>();

        for (var i = 0; i < points.Count; i++)
        {
            var start = points[i];
            var end = i + 1 < points.Count ? points[i + 1] - UInt128.One : Ceiling(isV6);
            if (end < start)
            {
                continue;
            }

            var segment = Merge(layers, places, start, end);
            if (segment is not { } value)
            {
                continue;
            }

            if (result.Count > 0
                && result[^1] is { } last
                && last.End + UInt128.One == start
                && last.Country == value.Country
                && last.Asn == value.Asn
                && last.Traits == value.Traits
                && last.Location == value.Location)
            {
                result[^1] = last with { End = end };
            }
            else
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static UInt128 Ceiling(bool isV6) => isV6 ? UInt128.MaxValue : uint.MaxValue;

    private static Segment? Merge(List<Normalized> layers, PlaceTable places, UInt128 start, UInt128 end)
    {
        string? country = null;
        uint asn = 0;
        var flags = NetworkTraits.None;
        double? latitude = null;
        double? longitude = null;
        string? region = null;
        string? city = null;
        var locationSource = LocationSource.None;

        foreach (var layer in layers)
        {
            if (layer.At(start) is not { } entry)
            {
                continue;
            }

            flags |= entry.Traits;

            if (asn == 0)
            {
                asn = entry.Asn;
            }

            var contributes = entry.CountryCode is not null || entry.HasPlace;
            if (contributes && locationSource == LocationSource.None && layer.Source != LocationSource.None)
            {
                locationSource = layer.Source;
            }

            country ??= entry.CountryCode;
            region ??= entry.Region;
            city ??= entry.City;
            if (latitude is null && entry.Latitude is not null)
            {
                latitude = entry.Latitude;
                longitude = entry.Longitude;
            }
        }

        var packedCountry = AtlasFormat.PackCountry(country);
        var location = latitude is not null || region is not null || city is not null
            ? places.Intern(latitude, longitude, region, city)
            : 0u;

        if (packedCountry == 0 && asn == 0 && flags == NetworkTraits.None && location == 0)
        {
            return null;
        }

        return new Segment(start, end, packedCountry, asn, AtlasFormat.PackTraits(flags, locationSource), location);
    }

    private static List<UInt128> CutPoints(List<Normalized> layers)
    {
        var capacity = 0;
        foreach (var layer in layers)
        {
            capacity += layer.Entries.Count * 2;
        }

        var points = new List<UInt128>(capacity);
        foreach (var layer in layers)
        {
            foreach (var entry in layer.Entries)
            {
                points.Add(entry.Start);
                if (entry.End != UInt128.MaxValue)
                {
                    points.Add(entry.End + UInt128.One);
                }
            }
        }

        Dedupe(points);
        return points;
    }

    /// <summary>
    /// Sorted, non-overlapping copy of one layer, resolving overlaps field by
    /// field: each answer comes from the most specific prefix that actually
    /// makes that claim.
    /// <para>
    /// Taking the whole payload from the most specific prefix is not enough, and
    /// the cloud files show why in both directions. AWS publishes a /24 in
    /// Frankfurt inside a /12 marked GLOBAL — let the /12 win and the region is
    /// lost. Azure publishes a narrow prefix for a region this build has never
    /// heard of, sitting inside a wider one it has — let the narrow one win and
    /// a known country is replaced by nothing. Neither prefix is wrong; each is
    /// specific about a different thing. So the country comes from the smallest
    /// prefix that names a country, the coordinates from the smallest that has
    /// coordinates, and the traits from all of them at once, because
    /// "datacenter" does not stop being true at a prefix boundary.
    /// </para>
    /// </summary>
    private static List<AtlasEntry> Normalize(IEnumerable<AtlasEntry> entries)
    {
        var sorted = entries
            .Where(entry => entry.Start <= entry.End && !entry.IsEmpty)
            .OrderBy(entry => entry.Start)
            .ThenBy(entry => entry.End - entry.Start)
            .ToList();

        if (sorted.Count == 0)
        {
            return [];
        }

        var points = new List<UInt128>(sorted.Count * 2);
        foreach (var entry in sorted)
        {
            points.Add(entry.Start);
            if (entry.End != UInt128.MaxValue)
            {
                points.Add(entry.End + UInt128.One);
            }
        }

        Dedupe(points);

        var active = new List<AtlasEntry>();
        var result = new List<AtlasEntry>();
        var next = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var start = points[i];
            while (next < sorted.Count && sorted[next].Start <= start)
            {
                active.Add(sorted[next]);
                next++;
            }

            var merged = Coalesce(active, start);
            if (merged is not { } value)
            {
                continue;
            }

            var end = i + 1 < points.Count ? points[i + 1] - UInt128.One : UInt128.MaxValue;
            value = value with { Start = start, End = end };

            if (result.Count > 0 && result[^1].End + UInt128.One == start && Same(result[^1], value))
            {
                result[^1] = result[^1] with { End = end };
            }
            else
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>
    /// Drops the entries that no longer cover the point and folds the rest into
    /// one, preferring the smallest prefix that has something to say per field.
    /// </summary>
    private static AtlasEntry? Coalesce(List<AtlasEntry> active, UInt128 point)
    {
        var traits = NetworkTraits.None;
        string? country = null;
        string? region = null;
        string? city = null;
        double? latitude = null;
        double? longitude = null;
        uint asn = 0;
        var countryWidth = UInt128.MaxValue;
        var regionWidth = UInt128.MaxValue;
        var cityWidth = UInt128.MaxValue;
        var pointWidth = UInt128.MaxValue;
        var asnWidth = UInt128.MaxValue;
        var isV6 = false;
        var any = false;

        var keep = 0;
        for (var i = 0; i < active.Count; i++)
        {
            var entry = active[i];
            if (entry.End < point)
            {
                continue;
            }

            active[keep++] = entry;
            any = true;
            isV6 = entry.IsV6;
            traits |= entry.Traits;

            var width = entry.End - entry.Start;
            if (entry.CountryCode is not null && width < countryWidth)
            {
                (country, countryWidth) = (entry.CountryCode, width);
            }

            if (entry.Region is not null && width < regionWidth)
            {
                (region, regionWidth) = (entry.Region, width);
            }

            if (entry.City is not null && width < cityWidth)
            {
                (city, cityWidth) = (entry.City, width);
            }

            if (entry.Latitude is not null && width < pointWidth)
            {
                (latitude, longitude, pointWidth) = (entry.Latitude, entry.Longitude, width);
            }

            if (entry.Asn != 0 && width < asnWidth)
            {
                (asn, asnWidth) = (entry.Asn, width);
            }
        }

        active.RemoveRange(keep, active.Count - keep);
        return any
            ? new AtlasEntry(isV6, point, point, country, asn, traits, latitude, longitude, region, city)
            : null;
    }

    /// <summary>Whether two entries carry the same payload, so they can be merged.</summary>
    private static bool Same(AtlasEntry left, AtlasEntry right) =>
        left.CountryCode == right.CountryCode
        && left.Asn == right.Asn
        && left.Traits == right.Traits
        && left.Latitude.Equals(right.Latitude)
        && left.Longitude.Equals(right.Longitude)
        && left.Region == right.Region
        && left.City == right.City;

    /// <summary>Sorts a list of cut points and drops the duplicates in place.</summary>
    private static void Dedupe(List<UInt128> points)
    {
        points.Sort();
        var unique = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if (i == 0 || points[i] != points[unique - 1])
            {
                points[unique++] = points[i];
            }
        }

        points.RemoveRange(unique, points.Count - unique);
    }

    /// <summary>
    /// Interns distinct places while the sweep runs, and hands each segment back
    /// as the record the writer takes. The writer interns again on its own side;
    /// this table exists so the sweep can compare places cheaply by id and so the
    /// build report can say how many distinct ones there were.
    /// </summary>
    private sealed class PlaceTable
    {
        private readonly Dictionary<(float, float, string?, string?), uint> _index = [];
        private readonly List<(double? Latitude, double? Longitude, string? Region, string? City)> _places = [];

        public int Count => _places.Count;

        public uint Intern(double? latitude, double? longitude, string? region, string? city)
        {
            var key = ((float)(latitude ?? double.NaN), (float)(longitude ?? double.NaN), region, city);
            if (_index.TryGetValue(key, out var existing))
            {
                return existing;
            }

            _places.Add((latitude, longitude, region, city));
            var id = (uint)_places.Count;
            _index[key] = id;
            return id;
        }

        public AtlasRecord Describe(Segment segment)
        {
            var record = new AtlasRecord(
                AtlasFormat.UnpackCountry(segment.Country),
                segment.Asn,
                AtlasFormat.UnpackTraits(segment.Traits),
                AtlasFormat.UnpackSource(segment.Traits));

            if (segment.Location == 0)
            {
                return record;
            }

            var place = _places[(int)(segment.Location - 1)];
            return record with
            {
                Latitude = place.Latitude,
                Longitude = place.Longitude,
                Region = place.Region,
                City = place.City,
            };
        }
    }
}
