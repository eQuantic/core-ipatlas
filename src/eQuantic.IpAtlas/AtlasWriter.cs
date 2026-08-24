using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace eQuantic.IpAtlas;

/// <summary>
/// Writes a dataset that <see cref="IpAtlasDatabase"/> will read.
/// <para>
/// The format is a header, a section table, the sections and a checksum, and
/// assembling one by hand means computing section offsets, interning places and
/// strings, and putting the CRC in the right place. Before this existed, anyone
/// who wanted a small deterministic dataset — for a test suite, for a fixture,
/// for a fallback — had to reimplement all of that from the format
/// documentation, and get to do it again at the next format version. It lives
/// beside the reader on purpose: a format whose writer and reader are maintained
/// apart is a format that drifts.
/// </para>
/// <para>
/// The writer will not produce a file the reader would reject. Ranges are sorted
/// for you, and overlapping or inverted ranges are refused at
/// <see cref="WriteTo"/> with a message naming the pair.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var writer = new AtlasWriter("test fixture", DateTimeOffset.UnixEpoch);
/// writer.AddPrefix("45.10.0.0/24", new AtlasRecord("PT", 1930));
/// writer.AddPrefix("2a01:4f8::/32", new AtlasRecord("DE", 24940, NetworkTraits.Hosting));
///
/// using var file = File.Create("fixture.eqatlas");
/// writer.WriteTo(file);
/// </code>
/// </example>
public sealed class AtlasWriter
{
    // Records are packed on the way in rather than held whole until WriteTo.
    // A dataset can be a million ranges, and keeping four string references and
    // two nullable doubles per range alive for the duration costs hundreds of
    // megabytes on a build that already needs gigabytes.
    private readonly List<Row<uint>> _v4 = [];
    private readonly List<Row<UInt128>> _v6 = [];
    private readonly Dictionary<(float, float, string?, string?), uint> _placeIndex = [];
    private readonly List<(float Latitude, float Longitude, string? Region, string? City)> _places = [];
    private readonly string _source;
    private readonly DateTimeOffset _builtAt;

    /// <summary>Starts a dataset.</summary>
    /// <param name="source">What it was built from. Travels in the header and is shown by <c>eqatlas verify</c>.</param>
    /// <param name="builtAt">When. Pass a fixed value for a reproducible file.</param>
    public AtlasWriter(string source, DateTimeOffset builtAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _builtAt = builtAt;
    }

    /// <summary>How many IPv4 ranges have been added.</summary>
    public int V4Count => _v4.Count;

    /// <summary>How many IPv6 ranges have been added.</summary>
    public int V6Count => _v6.Count;

    /// <summary>Adds an IPv4 range, given as big-endian integers.</summary>
    public void AddV4(uint start, uint end, AtlasRecord record)
    {
        if (start > end)
        {
            throw new ArgumentException($"The range {start}-{end} ends before it starts.", nameof(start));
        }

        if (!record.IsEmpty)
        {
            _v4.Add(Pack(start, end, record));
        }
    }

    /// <summary>Adds an IPv6 range, given as big-endian integers.</summary>
    public void AddV6(UInt128 start, UInt128 end, AtlasRecord record)
    {
        if (start > end)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(start));
        }

        if (!record.IsEmpty)
        {
            _v6.Add(Pack(start, end, record));
        }
    }

    /// <summary>Adds a range given as two addresses, which must be of the same family.</summary>
    public void Add(IPAddress start, IPAddress end, AtlasRecord record)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        if (start.AddressFamily != end.AddressFamily)
        {
            throw new ArgumentException("The two addresses are of different families.", nameof(end));
        }

        if (start.AddressFamily == AddressFamily.InterNetwork)
        {
            AddV4((uint)ToNumber(start), (uint)ToNumber(end), record);
        }
        else
        {
            AddV6(ToNumber(start), ToNumber(end), record);
        }
    }

    /// <summary>
    /// Adds a range given as a CIDR prefix, or a single address, which is the
    /// shortest way to write a fixture.
    /// </summary>
    /// <returns>False when the prefix does not parse; nothing is added.</returns>
    public bool AddPrefix(string prefix, AtlasRecord record)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var slash = prefix.AsSpan().IndexOf('/');
        IPAddress? address;
        int bits;
        if (slash < 0)
        {
            if (!IPAddress.TryParse(prefix, out address))
            {
                return false;
            }

            bits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        }
        else if (!IPAddress.TryParse(prefix.AsSpan(0, slash), out address)
            || !int.TryParse(prefix.AsSpan(slash + 1), out bits))
        {
            return false;
        }

        var isV6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var width = isV6 ? 128 : 32;
        if (bits < 0 || bits > width)
        {
            return false;
        }

        var hostBits = width - bits;
        var size = hostBits >= 128 ? UInt128.MaxValue : (UInt128.One << hostBits) - UInt128.One;
        var start = ToNumber(address) & ~size;

        if (isV6)
        {
            AddV6(start, start | size, record);
        }
        else
        {
            AddV4((uint)start, (uint)(start | size), record);
        }

        return true;
    }

    /// <summary>Writes the dataset.</summary>
    /// <exception cref="InvalidOperationException">Two ranges overlap, so the file would be unreadable.</exception>
    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        _v4.Sort((left, right) => left.Start.CompareTo(right.Start));
        _v6.Sort((left, right) => left.Start.CompareTo(right.Start));
        RequireDisjoint(_v4.Select(row => ((UInt128)row.Start, (UInt128)row.End)), "IPv4");
        RequireDisjoint(_v6.Select(row => (row.Start, row.End)), "IPv6");

        var v4Bytes = new byte[_v4.Count * AtlasFormat.V4RecordSize];
        for (var i = 0; i < _v4.Count; i++)
        {
            var row = _v4[i];
            AtlasFormat.WriteV4Record(
                v4Bytes.AsSpan(i * AtlasFormat.V4RecordSize),
                row.Start, row.End, row.Country, row.Asn, row.Traits, row.Location);
        }

        var v6Bytes = new byte[_v6.Count * AtlasFormat.V6RecordSize];
        for (var i = 0; i < _v6.Count; i++)
        {
            var row = _v6[i];
            AtlasFormat.WriteV6Record(
                v6Bytes.AsSpan(i * AtlasFormat.V6RecordSize),
                row.Start, row.End, row.Country, row.Asn, row.Traits, row.Location);
        }

        var (locationBytes, stringBytes) = SerializePlaces();
        var offset = (long)AtlasFormat.HeaderSize(_source, 4);
        var sections = new List<AtlasSection>(4);

        void Place(AtlasSectionKind kind, int count, long length)
        {
            sections.Add(new AtlasSection(kind, count, offset, length));
            offset += length;
        }

        Place(AtlasSectionKind.V4Ranges, _v4.Count, v4Bytes.Length);
        Place(AtlasSectionKind.V6Ranges, _v6.Count, v6Bytes.Length);
        Place(AtlasSectionKind.Locations, _places.Count, locationBytes.Length);
        Place(AtlasSectionKind.Strings, stringBytes.Length, stringBytes.Length);

        var header = new MemoryStream();
        AtlasFormat.WriteHeader(header, _builtAt, _source, sections);
        var headerBytes = header.ToArray();

        var state = Crc32.Begin();
        foreach (var block in new[] { headerBytes, v4Bytes, v6Bytes, locationBytes, stringBytes })
        {
            state = Crc32.Update(state, block);
            destination.Write(block, 0, block.Length);
        }

        Span<byte> checksum = stackalloc byte[AtlasFormat.ChecksumSize];
        BinaryPrimitives.WriteUInt32LittleEndian(checksum, Crc32.Finish(state));
        destination.Write(checksum);
    }

    /// <summary>One range, already packed into the shape the file stores.</summary>
    private readonly record struct Row<T>(T Start, T End, ushort Country, uint Asn, ushort Traits, uint Location);

    private Row<T> Pack<T>(T start, T end, AtlasRecord record) =>
        new(start, end,
            AtlasFormat.PackCountry(record.CountryCode),
            record.Asn,
            AtlasFormat.PackTraits(record.Traits, record.LocationSource),
            Intern(record));

    private static void RequireDisjoint(IEnumerable<(UInt128 Start, UInt128 End)> ranges, string family)
    {
        var previous = default((UInt128 Start, UInt128 End)?);
        foreach (var range in ranges)
        {
            if (previous is { } last && range.Start <= last.End)
            {
                throw new InvalidOperationException(
                    $"Two {family} ranges overlap, and a dataset must be non-overlapping to be searchable: "
                    + $"[{last.Start}, {last.End}] and [{range.Start}, {range.End}].");
            }

            previous = range;
        }
    }

    private uint Intern(AtlasRecord record)
    {
        if (!record.HasPlace)
        {
            return 0;
        }

        var key = ((float)(record.Latitude ?? double.NaN), (float)(record.Longitude ?? double.NaN),
            record.Region, record.City);
        if (_placeIndex.TryGetValue(key, out var existing))
        {
            return existing;
        }

        _places.Add((key.Item1, key.Item2, record.Region, record.City));
        var id = (uint)_places.Count;
        _placeIndex[key] = id;
        return id;
    }

    private (byte[] Locations, byte[] Strings) SerializePlaces()
    {
        var blob = new List<byte>();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);

        uint InternString(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            if (offsets.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > byte.MaxValue)
            {
                bytes = bytes[..byte.MaxValue];
            }

            var offset = (uint)blob.Count + 1; // one-based, so zero can mean "no string"
            blob.Add((byte)bytes.Length);
            blob.AddRange(bytes);
            offsets[value] = offset;
            return offset;
        }

        var locations = new byte[_places.Count * AtlasFormat.LocationRecordSize];
        for (var i = 0; i < _places.Count; i++)
        {
            var place = _places[i];
            AtlasFormat.WriteLocationRecord(
                locations.AsSpan(i * AtlasFormat.LocationRecordSize),
                place.Latitude, place.Longitude, InternString(place.Region), InternString(place.City));
        }

        return (locations, blob.ToArray());
    }

    private static UInt128 ToNumber(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out var written);
        return written == 16
            ? BinaryPrimitives.ReadUInt128BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes[..written]);
    }
}
