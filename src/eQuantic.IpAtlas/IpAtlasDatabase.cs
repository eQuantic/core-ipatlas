using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace eQuantic.IpAtlas;

/// <summary>
/// An immutable, fully in-memory .eqatlas dataset. Loading reads the file once,
/// verifies its checksum and internal consistency, then parses it into
/// structure-of-arrays form so a lookup is one binary search over contiguous
/// range starts — tens of nanoseconds, no allocation, safe from any thread.
/// Swap datasets by loading a new instance and replacing the reference.
/// <para>
/// Everything a file claims about itself is checked before it is believed:
/// counts against the real file length, section bounds against the buffer,
/// ranges against their own ordering. A dataset is an input like any other,
/// and a reader that trusts its header is one corrupt download away from an
/// out-of-memory crash or a silently wrong answer.
/// </para>
/// </summary>
public sealed class IpAtlasDatabase
{
    private readonly uint[] _v4Starts;
    private readonly uint[] _v4Ends;
    private readonly ushort[] _v4Countries;
    private readonly uint[] _v4Asns;
    private readonly ushort[] _v4Flags;
    private readonly uint[] _v4Locations;

    private readonly UInt128[] _v6Starts;
    private readonly UInt128[] _v6Ends;
    private readonly ushort[] _v6Countries;
    private readonly uint[] _v6Asns;
    private readonly ushort[] _v6Flags;
    private readonly uint[] _v6Locations;

    private readonly float[] _locationLatitudes;
    private readonly float[] _locationLongitudes;
    private readonly string?[] _locationRegions;
    private readonly string?[] _locationCities;

    /// <summary>When the dataset was compiled.</summary>
    public DateTimeOffset BuiltAt { get; }

    /// <summary>What the compiler said it was built from.</summary>
    public string Source { get; }

    /// <summary>The layout version of the file this was loaded from.</summary>
    public int FormatVersion { get; }

    /// <summary>Loaded IPv4 ranges.</summary>
    public int V4RangeCount => _v4Starts.Length;

    /// <summary>Loaded IPv6 ranges.</summary>
    public int V6RangeCount => _v6Starts.Length;

    /// <summary>Distinct locations the ranges point at.</summary>
    public int LocationCount => _locationLatitudes.Length;

    /// <summary>How old the dataset is, against the clock right now.</summary>
    public TimeSpan Age => DateTimeOffset.UtcNow - BuiltAt;

    private IpAtlasDatabase(Layout layout)
    {
        BuiltAt = layout.BuiltAt;
        Source = layout.Source;
        FormatVersion = layout.FormatVersion;
        _v4Starts = layout.V4Starts;
        _v4Ends = layout.V4Ends;
        _v4Countries = layout.V4Countries;
        _v4Asns = layout.V4Asns;
        _v4Flags = layout.V4Flags;
        _v4Locations = layout.V4Locations;
        _v6Starts = layout.V6Starts;
        _v6Ends = layout.V6Ends;
        _v6Countries = layout.V6Countries;
        _v6Asns = layout.V6Asns;
        _v6Flags = layout.V6Flags;
        _v6Locations = layout.V6Locations;
        _locationLatitudes = layout.LocationLatitudes;
        _locationLongitudes = layout.LocationLongitudes;
        _locationRegions = layout.LocationRegions;
        _locationCities = layout.LocationCities;
    }

    /// <summary>Loads a dataset from a file.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable, intact dataset.</exception>
    public static IpAtlasDatabase Open(string path)
    {
        using var stream = File.OpenRead(path);
        try
        {
            return Open(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"'{path}' is not a usable .eqatlas dataset: {ex.Message}", ex);
        }
    }

    /// <summary>Loads a dataset from a stream.</summary>
    /// <exception cref="InvalidDataException">The stream is not a readable, intact dataset.</exception>
    public static IpAtlasDatabase Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var (buffer, length) = ReadAll(stream);
        try
        {
            return new IpAtlasDatabase(Parse(buffer.AsSpan(0, length)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Loads a dataset, answering false instead of throwing when the file is
    /// missing, unreadable or corrupt — for callers whose fallback is to keep
    /// serving the dataset they already have.
    /// </summary>
    public static bool TryOpen(string path, out IpAtlasDatabase? database, out string? error)
    {
        try
        {
            database = Open(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            database = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Looks a textual address up; unparsable input answers unknown.</summary>
    public IpInfo Lookup(string ipAddress) =>
        IPAddress.TryParse(ipAddress, out var parsed) ? Lookup(parsed) : IpInfo.Unknown;

    /// <summary>Looks an address up: scope, country, ASN, network kind and location.</summary>
    public IpInfo Lookup(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (!address.TryWriteBytes(bytes, out _))
            {
                return IpInfo.Unknown;
            }

            var value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var scope = IpScopes.ClassifyV4(value);
            return scope != IpScope.Public
                ? new IpInfo(null, null, NetworkTraits.None, scope)
                : Build(FindV4(value), _v4Countries, _v4Asns, _v4Flags, _v4Locations);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out _))
            {
                return IpInfo.Unknown;
            }

            var value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
            var scope = IpScopes.ClassifyV6(value);
            return scope != IpScope.Public
                ? new IpInfo(null, null, NetworkTraits.None, scope)
                : Build(FindV6(value), _v6Countries, _v6Asns, _v6Flags, _v6Locations);
        }

        return IpInfo.Unknown;
    }

    private int FindV4(uint value)
    {
        var index = UpperBound(_v4Starts, value);
        return index >= 0 && value <= _v4Ends[index] ? index : -1;
    }

    private int FindV6(UInt128 value)
    {
        var index = UpperBound(_v6Starts, value);
        return index >= 0 && value <= _v6Ends[index] ? index : -1;
    }

    private IpInfo Build(int index, ushort[] countries, uint[] asns, ushort[] flags, uint[] locations)
    {
        if (index < 0)
        {
            return IpInfo.Unknown;
        }

        var packed = flags[index];
        var locationId = locations[index];
        IpLocation? location = null;
        if (locationId != 0 && locationId <= (uint)_locationLatitudes.Length)
        {
            var slot = (int)(locationId - 1);
            var latitude = _locationLatitudes[slot];
            var longitude = _locationLongitudes[slot];
            location = new IpLocation(
                float.IsNaN(latitude) ? null : latitude,
                float.IsNaN(longitude) ? null : longitude,
                _locationRegions[slot],
                _locationCities[slot],
                AtlasFormat.UnpackSource(packed));
        }

        return new IpInfo(
            AtlasFormat.UnpackCountry(countries[index]),
            asns[index] == 0 ? null : asns[index],
            AtlasFormat.UnpackTraits(packed),
            IpScope.Public,
            location);
    }

    /// <summary>Index of the last element &lt;= value, or -1 when value precedes them all.</summary>
    private static int UpperBound<T>(T[] sorted, T value) where T : IComparable<T>
    {
        var index = Array.BinarySearch(sorted, value);
        return index >= 0 ? index : ~index - 1;
    }

    private static (byte[] Buffer, int Length) ReadAll(Stream stream)
    {
        var expected = stream.CanSeek ? stream.Length - stream.Position : -1;
        if (expected > AtlasFormat.MaxDatasetBytes)
        {
            throw new InvalidDataException(
                $"the dataset is {expected:N0} bytes, over the {AtlasFormat.MaxDatasetBytes:N0} byte limit.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(expected > 0 ? (int)expected : 64 * 1024);
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if ((long)buffer.Length >= AtlasFormat.MaxDatasetBytes)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw new InvalidDataException("the dataset is over the size limit.");
                }

                var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                buffer.AsSpan(0, length).CopyTo(grown);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = grown;
            }

            var read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                return (buffer, length);
            }

            length += read;
        }
    }

    private sealed record Layout(
        DateTimeOffset BuiltAt, string Source, int FormatVersion,
        uint[] V4Starts, uint[] V4Ends, ushort[] V4Countries, uint[] V4Asns, ushort[] V4Flags, uint[] V4Locations,
        UInt128[] V6Starts, UInt128[] V6Ends, ushort[] V6Countries, uint[] V6Asns, ushort[] V6Flags, uint[] V6Locations,
        float[] LocationLatitudes, float[] LocationLongitudes, string?[] LocationRegions, string?[] LocationCities);

    private static Layout Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 18)
        {
            throw new InvalidDataException("the file is too short to be a dataset.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != AtlasFormat.Magic)
        {
            throw new InvalidDataException("not an .eqatlas dataset (bad magic).");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (version is < AtlasFormat.MinReadableVersion or > AtlasFormat.Version)
        {
            throw new InvalidDataException(
                $"unsupported layout version {version}; this build reads {AtlasFormat.MinReadableVersion} " +
                $"to {AtlasFormat.Version}. Rebuild the dataset, or upgrade eQuantic.IpAtlas.");
        }

        var builtAt = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(file[8..]));
        var sourceLength = BinaryPrimitives.ReadUInt16LittleEndian(file[16..]);
        if (18 + sourceLength > file.Length)
        {
            throw new InvalidDataException("the header runs past the end of the file.");
        }

        var source = Encoding.UTF8.GetString(file.Slice(18, sourceLength));
        var cursor = 18 + sourceLength;

        return version == 1
            ? ParseV1(file, cursor, builtAt, source)
            : ParseV2(file, cursor, builtAt, source);
    }

    private static Layout ParseV2(ReadOnlySpan<byte> file, int cursor, DateTimeOffset builtAt, string source)
    {
        if (file.Length < cursor + 1 + AtlasFormat.ChecksumSize)
        {
            throw new InvalidDataException("the file is truncated before its section table.");
        }

        var sectionCount = file[cursor++];
        var tableBytes = sectionCount * AtlasFormat.SectionEntrySize;
        if (file.Length < cursor + tableBytes + AtlasFormat.ChecksumSize)
        {
            throw new InvalidDataException("the section table runs past the end of the file.");
        }

        var body = file[..^AtlasFormat.ChecksumSize];
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(file[^AtlasFormat.ChecksumSize..]);
        var actual = Crc32.Compute(body);
        if (stored != actual)
        {
            throw new InvalidDataException(
                $"checksum mismatch (header says {stored:X8}, contents are {actual:X8}) — the dataset is corrupt.");
        }

        var sections = new List<AtlasSection>(sectionCount);
        for (var i = 0; i < sectionCount; i++)
        {
            var entry = file.Slice(cursor + (i * AtlasFormat.SectionEntrySize), AtlasFormat.SectionEntrySize);
            var section = new AtlasSection(
                (AtlasSectionKind)entry[0],
                BinaryPrimitives.ReadInt32LittleEndian(entry[1..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[5..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[13..]));

            if (section.Count is < 0 or > AtlasFormat.MaxRecordCount)
            {
                throw new InvalidDataException($"section {section.Kind} claims {section.Count:N0} records.");
            }

            if (section.Offset < 0 || section.Length < 0 || section.Offset + section.Length > body.Length)
            {
                throw new InvalidDataException($"section {section.Kind} lies outside the file.");
            }

            sections.Add(section);
        }

        var strings = Find(sections, AtlasSectionKind.Strings) is { } stringSection
            ? file.Slice((int)stringSection.Offset, (int)stringSection.Length)
            : default;

        var (latitudes, longitudes, regions, cities) = ReadLocations(file, Find(sections, AtlasSectionKind.Locations), strings);
        var v4 = ReadV4(file, Find(sections, AtlasSectionKind.V4Ranges));
        var v6 = ReadV6(file, Find(sections, AtlasSectionKind.V6Ranges));

        return new Layout(
            builtAt, source, 2,
            v4.Starts, v4.Ends, v4.Countries, v4.Asns, v4.Flags, v4.Locations,
            v6.Starts, v6.Ends, v6.Countries, v6.Asns, v6.Flags, v6.Locations,
            latitudes, longitudes, regions, cities);
    }

    private static AtlasSection? Find(List<AtlasSection> sections, AtlasSectionKind kind)
    {
        foreach (var section in sections)
        {
            if (section.Kind == kind)
            {
                return section;
            }
        }

        return null;
    }

    private static (uint[] Starts, uint[] Ends, ushort[] Countries, uint[] Asns, ushort[] Flags, uint[] Locations)
        ReadV4(ReadOnlySpan<byte> file, AtlasSection? section)
    {
        if (section is not { } s || s.Count == 0)
        {
            return ([], [], [], [], [], []);
        }

        RequireLength(s, AtlasFormat.V4RecordSize);
        var starts = new uint[s.Count];
        var ends = new uint[s.Count];
        var countries = new ushort[s.Count];
        var asns = new uint[s.Count];
        var flags = new ushort[s.Count];
        var locations = new uint[s.Count];

        var data = file.Slice((int)s.Offset, (int)s.Length);
        for (var i = 0; i < s.Count; i++)
        {
            var record = data.Slice(i * AtlasFormat.V4RecordSize, AtlasFormat.V4RecordSize);
            starts[i] = BinaryPrimitives.ReadUInt32LittleEndian(record);
            ends[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            countries[i] = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            asns[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
            flags[i] = BinaryPrimitives.ReadUInt16LittleEndian(record[14..]);
            locations[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        }

        RequireSorted<uint>(starts, ends, "IPv4");
        return (starts, ends, countries, asns, flags, locations);
    }

    private static (UInt128[] Starts, UInt128[] Ends, ushort[] Countries, uint[] Asns, ushort[] Flags, uint[] Locations)
        ReadV6(ReadOnlySpan<byte> file, AtlasSection? section)
    {
        if (section is not { } s || s.Count == 0)
        {
            return ([], [], [], [], [], []);
        }

        RequireLength(s, AtlasFormat.V6RecordSize);
        var starts = new UInt128[s.Count];
        var ends = new UInt128[s.Count];
        var countries = new ushort[s.Count];
        var asns = new uint[s.Count];
        var flags = new ushort[s.Count];
        var locations = new uint[s.Count];

        var data = file.Slice((int)s.Offset, (int)s.Length);
        for (var i = 0; i < s.Count; i++)
        {
            var record = data.Slice(i * AtlasFormat.V6RecordSize, AtlasFormat.V6RecordSize);
            starts[i] = BinaryPrimitives.ReadUInt128BigEndian(record);
            ends[i] = BinaryPrimitives.ReadUInt128BigEndian(record[16..]);
            countries[i] = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            asns[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[34..]);
            flags[i] = BinaryPrimitives.ReadUInt16LittleEndian(record[38..]);
            locations[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[40..]);
        }

        RequireSorted<UInt128>(starts, ends, "IPv6");
        return (starts, ends, countries, asns, flags, locations);
    }

    private static (float[] Latitudes, float[] Longitudes, string?[] Regions, string?[] Cities)
        ReadLocations(ReadOnlySpan<byte> file, AtlasSection? section, ReadOnlySpan<byte> strings)
    {
        if (section is not { } s || s.Count == 0)
        {
            return ([], [], [], []);
        }

        RequireLength(s, AtlasFormat.LocationRecordSize);
        var latitudes = new float[s.Count];
        var longitudes = new float[s.Count];
        var regions = new string?[s.Count];
        var cities = new string?[s.Count];

        var data = file.Slice((int)s.Offset, (int)s.Length);
        for (var i = 0; i < s.Count; i++)
        {
            var record = data.Slice(i * AtlasFormat.LocationRecordSize, AtlasFormat.LocationRecordSize);
            latitudes[i] = BinaryPrimitives.ReadSingleLittleEndian(record);
            longitudes[i] = BinaryPrimitives.ReadSingleLittleEndian(record[4..]);
            regions[i] = ReadString(strings, BinaryPrimitives.ReadUInt32LittleEndian(record[8..]));
            cities[i] = ReadString(strings, BinaryPrimitives.ReadUInt32LittleEndian(record[12..]));
        }

        return (latitudes, longitudes, regions, cities);
    }

    private static string? ReadString(ReadOnlySpan<byte> strings, uint offset)
    {
        if (offset == 0 || offset > (uint)strings.Length)
        {
            return null;
        }

        var at = (int)(offset - 1);
        var length = strings[at];
        return at + 1 + length > strings.Length ? null : Encoding.UTF8.GetString(strings.Slice(at + 1, length));
    }

    private static void RequireLength(AtlasSection section, int recordSize)
    {
        if (section.Length != (long)section.Count * recordSize)
        {
            throw new InvalidDataException(
                $"section {section.Kind} holds {section.Length:N0} bytes for {section.Count:N0} records.");
        }
    }

    private static void RequireSorted<T>(T[] starts, T[] ends, string family) where T : IComparable<T>
    {
        for (var i = 0; i < starts.Length; i++)
        {
            if (starts[i].CompareTo(ends[i]) > 0)
            {
                throw new InvalidDataException($"{family} range {i} ends before it starts.");
            }

            if (i > 0 && starts[i - 1].CompareTo(starts[i]) >= 0)
            {
                throw new InvalidDataException($"{family} ranges are not in ascending order at {i}.");
            }
        }
    }

    private static Layout ParseV1(ReadOnlySpan<byte> file, int cursor, DateTimeOffset builtAt, string source)
    {
        if (file.Length < cursor + 8)
        {
            throw new InvalidDataException("the file is truncated before its record counts.");
        }

        var v4Count = BinaryPrimitives.ReadInt32LittleEndian(file[cursor..]);
        var v6Count = BinaryPrimitives.ReadInt32LittleEndian(file[(cursor + 4)..]);
        cursor += 8;

        if (v4Count is < 0 or > AtlasFormat.MaxRecordCount || v6Count is < 0 or > AtlasFormat.MaxRecordCount)
        {
            throw new InvalidDataException($"implausible record counts ({v4Count}, {v6Count}).");
        }

        var needed = ((long)v4Count * AtlasFormat.V1RecordSizeV4) + ((long)v6Count * AtlasFormat.V1RecordSizeV6);
        if (file.Length - cursor < needed)
        {
            throw new InvalidDataException(
                $"the file promises {needed:N0} bytes of records but holds {file.Length - cursor:N0}.");
        }

        var v4Starts = new uint[v4Count];
        var v4Ends = new uint[v4Count];
        var v4Countries = new ushort[v4Count];
        var v4Asns = new uint[v4Count];
        for (var i = 0; i < v4Count; i++)
        {
            var record = file.Slice(cursor + (i * AtlasFormat.V1RecordSizeV4), AtlasFormat.V1RecordSizeV4);
            v4Starts[i] = BinaryPrimitives.ReadUInt32LittleEndian(record);
            v4Ends[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            v4Countries[i] = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            v4Asns[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
        }

        cursor += v4Count * AtlasFormat.V1RecordSizeV4;
        var v6Starts = new UInt128[v6Count];
        var v6Ends = new UInt128[v6Count];
        var v6Countries = new ushort[v6Count];
        var v6Asns = new uint[v6Count];
        for (var i = 0; i < v6Count; i++)
        {
            var record = file.Slice(cursor + (i * AtlasFormat.V1RecordSizeV6), AtlasFormat.V1RecordSizeV6);
            v6Starts[i] = BinaryPrimitives.ReadUInt128BigEndian(record);
            v6Ends[i] = BinaryPrimitives.ReadUInt128BigEndian(record[16..]);
            v6Countries[i] = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            v6Asns[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[34..]);
        }

        return new Layout(
            builtAt, source, 1,
            v4Starts, v4Ends, v4Countries, v4Asns, new ushort[v4Count], new uint[v4Count],
            v6Starts, v6Ends, v6Countries, v6Asns, new ushort[v6Count], new uint[v6Count],
            [], [], [], []);
    }
}
