using System.Buffers.Binary;
using System.Text;

namespace eQuantic.IpAtlas;

/// <summary>What a section of an .eqatlas file holds. Readers skip kinds they do not know.</summary>
public enum AtlasSectionKind : byte
{
    /// <summary>Padding or a kind this reader has no name for.</summary>
    Unknown = 0,

    /// <summary>IPv4 range records, sorted by range start.</summary>
    V4Ranges = 1,

    /// <summary>IPv6 range records, sorted by range start.</summary>
    V6Ranges = 2,

    /// <summary>Location records that range records point into.</summary>
    Locations = 3,

    /// <summary>UTF-8 blob the location records take their names from.</summary>
    Strings = 4,
}

/// <summary>Where one section sits in the file.</summary>
/// <param name="Kind">What the section holds.</param>
/// <param name="Count">How many records it holds.</param>
/// <param name="Offset">Byte offset from the start of the file.</param>
/// <param name="Length">Length in bytes.</param>
public readonly record struct AtlasSection(AtlasSectionKind Kind, int Count, long Offset, long Length);

/// <summary>
/// The .eqatlas binary layout, shared by the compiler (writer) and the database
/// (reader).
/// <para>
/// Version 2 is a header, a section table, the sections, and a trailing CRC-32
/// over everything before it. The section table is the point: a reader skips
/// section kinds it does not recognise, so later datasets can carry city names
/// or new signals without breaking readers already deployed. Version 1 files
/// (a fixed header and two record blocks, no checksum) still load.
/// </para>
/// </summary>
public static class AtlasFormat
{
    /// <summary>File magic, "ATLS" in little-endian.</summary>
    public const uint Magic = 0x534C5441;

    /// <summary>The layout this library writes.</summary>
    public const ushort Version = 2;

    /// <summary>The oldest layout this library still reads.</summary>
    public const ushort MinReadableVersion = 1;

    /// <summary>Bytes per IPv4 record in a version 1 file.</summary>
    public const int V1RecordSizeV4 = 4 + 4 + 2 + 4;

    /// <summary>Bytes per IPv6 record in a version 1 file.</summary>
    public const int V1RecordSizeV6 = 16 + 16 + 2 + 4;

    /// <summary>Bytes per IPv4 record: start, end, country, ASN, flags, location.</summary>
    public const int V4RecordSize = 4 + 4 + 2 + 4 + 2 + 4;

    /// <summary>Bytes per IPv6 record: start, end, country, ASN, flags, location.</summary>
    public const int V6RecordSize = 16 + 16 + 2 + 4 + 2 + 4;

    /// <summary>Bytes per location record: latitude, longitude, region name, city name.</summary>
    public const int LocationRecordSize = 4 + 4 + 4 + 4;

    /// <summary>Bytes of one section-table entry.</summary>
    public const int SectionEntrySize = 1 + 4 + 8 + 8;

    /// <summary>Bytes of the trailing checksum.</summary>
    public const int ChecksumSize = 4;

    /// <summary>
    /// A ceiling on record counts, so a corrupt or hostile header cannot talk a
    /// reader into allocating gigabytes before it has read a single record. The
    /// whole routed internet is a few million ranges; 64 million is far above
    /// any real dataset and far below anything that hurts.
    /// </summary>
    public const int MaxRecordCount = 64 * 1024 * 1024;

    /// <summary>
    /// The largest dataset this reader will load. A full world dataset is tens
    /// of megabytes; a gigabyte is room to grow and still a wall a corrupt
    /// length field cannot walk a service through.
    /// </summary>
    public const long MaxDatasetBytes = 1024L * 1024 * 1024;

    /// <summary>Two ASCII letters into a ushort; zero when the code is not two letters A-Z.</summary>
    public static ushort PackCountry(string? countryCode)
    {
        if (countryCode is not { Length: 2 })
        {
            return 0;
        }

        var first = ToUpperAscii(countryCode[0]);
        var second = ToUpperAscii(countryCode[1]);
        return first == 0 || second == 0 ? (ushort)0 : (ushort)((first << 8) | second);
    }

    /// <summary>The inverse of <see cref="PackCountry"/>, from a cached table so lookups do not allocate.</summary>
    public static string? UnpackCountry(ushort packed) => CountryStrings.Get(packed);

    private static byte ToUpperAscii(char value) => value switch
    {
        >= 'A' and <= 'Z' => (byte)value,
        >= 'a' and <= 'z' => (byte)(value - 32),
        _ => 0,
    };

    /// <summary>Bytes the header occupies for a given source string and section count.</summary>
    public static int HeaderSize(string source, int sectionCount) =>
        4 + 2 + 2 + 8 + 2 + Encoding.UTF8.GetByteCount(source) + 1 + (sectionCount * SectionEntrySize);

    /// <summary>Writes the version 2 header and section table.</summary>
    public static void WriteHeader(
        Stream stream, DateTimeOffset builtAt, string source, IReadOnlyList<AtlasSection> sections)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sections);

        var sourceBytes = Encoding.UTF8.GetBytes(source);
        if (sourceBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The source description is too long.", nameof(source));
        }

        if (sections.Count > byte.MaxValue)
        {
            throw new ArgumentException("Too many sections.", nameof(sections));
        }

        Span<byte> header = stackalloc byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], builtAt.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], (ushort)sourceBytes.Length);
        stream.Write(header);
        stream.Write(sourceBytes);
        stream.WriteByte((byte)sections.Count);

        Span<byte> entry = stackalloc byte[SectionEntrySize];
        foreach (var section in sections)
        {
            entry[0] = (byte)section.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(entry[1..], section.Count);
            BinaryPrimitives.WriteInt64LittleEndian(entry[5..], section.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(entry[13..], section.Length);
            stream.Write(entry);
        }
    }

    /// <summary>Writes one IPv4 range record into a span.</summary>
    public static void WriteV4Record(
        Span<byte> destination, uint start, uint end, ushort country, uint asn, ushort flags, uint location)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, start);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], end);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], country);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[10..], asn);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], location);
    }

    /// <summary>Writes one IPv6 range record into a span.</summary>
    public static void WriteV6Record(
        Span<byte> destination, UInt128 start, UInt128 end, ushort country, uint asn, ushort flags, uint location)
    {
        BinaryPrimitives.WriteUInt128BigEndian(destination, start);
        BinaryPrimitives.WriteUInt128BigEndian(destination[16..], end);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[32..], country);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[34..], asn);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[38..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], location);
    }

    /// <summary>
    /// Packs the feature flags and the location's provenance into the one ushort
    /// the range record carries: flags in the low byte, source in the high byte.
    /// </summary>
    public static ushort PackFlags(IpFlags flags, LocationSource source) =>
        (ushort)(((byte)source << 8) | (byte)flags);

    /// <summary>The feature flags out of a packed range-record field.</summary>
    public static IpFlags UnpackFlags(ushort packed) => (IpFlags)(byte)(packed & 0xFF);

    /// <summary>The location provenance out of a packed range-record field.</summary>
    public static LocationSource UnpackSource(ushort packed) => (LocationSource)(byte)(packed >> 8);

    /// <summary>Writes one location record into a span.</summary>
    public static void WriteLocationRecord(
        Span<byte> destination, float latitude, float longitude, uint regionOffset, uint cityOffset)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination, latitude);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], longitude);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], regionOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], cityOffset);
    }
}
