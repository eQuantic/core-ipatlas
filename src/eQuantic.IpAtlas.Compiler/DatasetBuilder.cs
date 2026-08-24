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
        var (locationBytes, stringBytes) = places.Serialize();

        var sections = LaySections(source, v4.Count, v6.Count, places.Count, locationBytes.Length, stringBytes.Length);
        var headerStream = new MemoryStream();
        AtlasFormat.WriteHeader(headerStream, builtAt, source, sections);
        var header = headerStream.ToArray();

        var state = Crc32.Begin();
        Emit(output, ref state, header);

        var v4Bytes = new byte[v4.Count * AtlasFormat.V4RecordSize];
        for (var i = 0; i < v4.Count; i++)
        {
            var segment = v4[i];
            AtlasFormat.WriteV4Record(
                v4Bytes.AsSpan(i * AtlasFormat.V4RecordSize),
                (uint)segment.Start, (uint)segment.End,
                segment.Country, segment.Asn, segment.Flags, segment.Location);
        }

        Emit(output, ref state, v4Bytes);

        var v6Bytes = new byte[v6.Count * AtlasFormat.V6RecordSize];
        for (var i = 0; i < v6.Count; i++)
        {
            var segment = v6[i];
            AtlasFormat.WriteV6Record(
                v6Bytes.AsSpan(i * AtlasFormat.V6RecordSize),
                segment.Start, segment.End,
                segment.Country, segment.Asn, segment.Flags, segment.Location);
        }

        Emit(output, ref state, v6Bytes);
        Emit(output, ref state, locationBytes);
        Emit(output, ref state, stringBytes);

        Span<byte> checksum = stackalloc byte[AtlasFormat.ChecksumSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(checksum, Crc32.Finish(state));
        output.Write(checksum);

        var bytes = (long)header.Length + v4Bytes.Length + v6Bytes.Length
            + locationBytes.Length + stringBytes.Length + AtlasFormat.ChecksumSize;

        return new BuildReport(
            v4.Count, v6.Count, places.Count, bytes,
            Provenance(v4, v6, LocationSource.RegistryDelegation),
            Provenance(v4, v6, LocationSource.Geofeed),
            Provenance(v4, v6, LocationSource.CloudProvider));
    }

    private static int Provenance(List<Segment> v4, List<Segment> v6, LocationSource source)
    {
        var wanted = (ushort)((byte)source << 8);
        var count = 0;
        foreach (var segment in v4)
        {
            if ((segment.Flags & 0xFF00) == wanted && segment.Country != 0)
            {
                count++;
            }
        }

        foreach (var segment in v6)
        {
            if ((segment.Flags & 0xFF00) == wanted && segment.Country != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static void Emit(Stream output, ref uint state, byte[] bytes)
    {
        state = Crc32.Update(state, bytes);
        output.Write(bytes, 0, bytes.Length);
    }

    private static List<AtlasSection> LaySections(
        string source, int v4Count, int v6Count, int locationCount, int locationBytes, int stringBytes)
    {
        var offset = (long)AtlasFormat.HeaderSize(source, 4);
        var sections = new List<AtlasSection>(4);

        long Place(AtlasSectionKind kind, int count, long length)
        {
            sections.Add(new AtlasSection(kind, count, offset, length));
            return offset += length;
        }

        Place(AtlasSectionKind.V4Ranges, v4Count, (long)v4Count * AtlasFormat.V4RecordSize);
        Place(AtlasSectionKind.V6Ranges, v6Count, (long)v6Count * AtlasFormat.V6RecordSize);
        Place(AtlasSectionKind.Locations, locationCount, locationBytes);
        Place(AtlasSectionKind.Strings, stringBytes, stringBytes);
        return sections;
    }

    private readonly record struct Segment(UInt128 Start, UInt128 End, ushort Country, uint Asn, ushort Flags, uint Location);

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
                && last.Flags == value.Flags
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
        var flags = IpFlags.None;
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

            flags |= entry.Flags;

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

        if (packedCountry == 0 && asn == 0 && flags == IpFlags.None && location == 0)
        {
            return null;
        }

        return new Segment(start, end, packedCountry, asn, AtlasFormat.PackFlags(flags, locationSource), location);
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
    /// Sorted, non-overlapping copy of one layer, resolving overlaps the way
    /// routing does: the most specific prefix wins.
    /// <para>
    /// This matters more than it sounds. AWS publishes a /24 in Frankfurt and a
    /// /12 marked GLOBAL that contains it. Resolving by whichever started first
    /// let the /12 swallow the /24, and several hundred regional blocks fell
    /// back to the registry's answer of "United States". A smaller prefix is a
    /// more specific claim about a smaller piece of the internet, and it wins.
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

        // Smallest covering range first, with a stable tie-break so two builds
        // from the same inputs produce the same bytes.
        var active = new PriorityQueue<AtlasEntry, (UInt128 Size, UInt128 Start, int Order)>();
        var result = new List<AtlasEntry>();
        var next = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var start = points[i];
            while (next < sorted.Count && sorted[next].Start <= start)
            {
                var entry = sorted[next];
                active.Enqueue(entry, (entry.End - entry.Start, entry.Start, next));
                next++;
            }

            while (active.Count > 0 && active.Peek().End < start)
            {
                active.Dequeue();
            }

            if (active.Count == 0)
            {
                continue;
            }

            var end = i + 1 < points.Count ? points[i + 1] - UInt128.One : UInt128.MaxValue;
            var winner = active.Peek() with { Start = start, End = end };

            if (result.Count > 0
                && result[^1].End + UInt128.One == start
                && Same(result[^1], winner))
            {
                result[^1] = result[^1] with { End = end };
            }
            else
            {
                result.Add(winner);
            }
        }

        return result;
    }

    /// <summary>Whether two entries carry the same payload, so they can be merged.</summary>
    private static bool Same(AtlasEntry left, AtlasEntry right) =>
        left.CountryCode == right.CountryCode
        && left.Asn == right.Asn
        && left.Flags == right.Flags
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

    /// <summary>Interns distinct places and their names so each is written once.</summary>
    private sealed class PlaceTable
    {
        private readonly Dictionary<(float, float, string?, string?), uint> _index = [];
        private readonly List<(float Latitude, float Longitude, string? Region, string? City)> _places = [];
        private readonly Dictionary<string, uint> _strings = new(StringComparer.Ordinal);
        private readonly List<byte[]> _blob = [];
        private uint _blobLength;

        public int Count => _places.Count;

        public uint Intern(double? latitude, double? longitude, string? region, string? city)
        {
            var key = ((float)(latitude ?? double.NaN), (float)(longitude ?? double.NaN), region, city);
            if (_index.TryGetValue(key, out var existing))
            {
                return existing;
            }

            _places.Add((key.Item1, key.Item2, region, city));
            var id = (uint)_places.Count;
            _index[key] = id;
            return id;
        }

        public (byte[] Locations, byte[] Strings) Serialize()
        {
            var locations = new byte[_places.Count * AtlasFormat.LocationRecordSize];
            for (var i = 0; i < _places.Count; i++)
            {
                var place = _places[i];
                AtlasFormat.WriteLocationRecord(
                    locations.AsSpan(i * AtlasFormat.LocationRecordSize),
                    place.Latitude, place.Longitude, InternString(place.Region), InternString(place.City));
            }

            var strings = new byte[_blobLength];
            var at = 0;
            foreach (var chunk in _blob)
            {
                chunk.CopyTo(strings, at);
                at += chunk.Length;
            }

            return (locations, strings);
        }

        private uint InternString(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            if (_strings.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > byte.MaxValue)
            {
                bytes = bytes[..byte.MaxValue];
            }

            var chunk = new byte[bytes.Length + 1];
            chunk[0] = (byte)bytes.Length;
            bytes.CopyTo(chunk, 1);

            var offset = _blobLength + 1; // one-based, so zero can mean "no string"
            _blob.Add(chunk);
            _blobLength += (uint)chunk.Length;
            _strings[value] = offset;
            return offset;
        }
    }
}
