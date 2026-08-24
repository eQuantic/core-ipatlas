using System.Buffers.Binary;
using System.Text;

namespace eQuantic.IpIntel;

/// <summary>
/// The .eqip binary layout, shared by the compiler (writer) and the database
/// (reader). One flat file: a small header, then the IPv4 records, then the
/// IPv6 records, each section sorted by range start so lookups are a binary
/// search. Countries are two ASCII letters packed into a ushort; zero means
/// the range carries none.
/// </summary>
public static class EqipFormat
{
    /// <summary>File magic, "EQIP" in little-endian.</summary>
    public const uint Magic = 0x50495145;

    /// <summary>Current layout version.</summary>
    public const ushort Version = 1;

    /// <summary>Bytes per IPv4 record.</summary>
    public const int V4RecordSize = 4 + 4 + 2 + 4;

    /// <summary>Bytes per IPv6 record.</summary>
    public const int V6RecordSize = 16 + 16 + 2 + 4;

    /// <summary>Two ASCII letters into a ushort; zero for none.</summary>
    public static ushort PackCountry(string? countryCode) =>
        string.IsNullOrEmpty(countryCode) || countryCode.Length != 2
            ? (ushort)0
            : (ushort)((char.ToUpperInvariant(countryCode[0]) << 8) | char.ToUpperInvariant(countryCode[1]));

    /// <summary>The inverse of <see cref="PackCountry"/>.</summary>
    public static string? UnpackCountry(ushort packed) =>
        packed == 0 ? null : string.Create(2, packed, static (span, value) =>
        {
            span[0] = (char)(value >> 8);
            span[1] = (char)(value & 0xFF);
        });

    /// <summary>Writes the header; returns nothing the caller must track — counts travel in it.</summary>
    public static void WriteHeader(Stream stream, DateTimeOffset builtAt, string source, int v4Count, int v6Count)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        if (sourceBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The source description is too long.", nameof(source));
        }

        Span<byte> header = stackalloc byte[4 + 2 + 2 + 8 + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], builtAt.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], (ushort)sourceBytes.Length);
        stream.Write(header);
        stream.Write(sourceBytes);

        Span<byte> counts = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(counts, v4Count);
        BinaryPrimitives.WriteInt32LittleEndian(counts[4..], v6Count);
        stream.Write(counts);
    }

    /// <summary>Appends one IPv4 range record.</summary>
    public static void WriteV4Record(Stream stream, uint start, uint end, ushort country, uint asn)
    {
        Span<byte> record = stackalloc byte[V4RecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, start);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], end);
        BinaryPrimitives.WriteUInt16LittleEndian(record[8..], country);
        BinaryPrimitives.WriteUInt32LittleEndian(record[10..], asn);
        stream.Write(record);
    }

    /// <summary>Appends one IPv6 range record.</summary>
    public static void WriteV6Record(Stream stream, UInt128 start, UInt128 end, ushort country, uint asn)
    {
        Span<byte> record = stackalloc byte[V6RecordSize];
        BinaryPrimitives.WriteUInt128BigEndian(record, start);
        BinaryPrimitives.WriteUInt128BigEndian(record[16..], end);
        BinaryPrimitives.WriteUInt16LittleEndian(record[32..], country);
        BinaryPrimitives.WriteUInt32LittleEndian(record[34..], asn);
        stream.Write(record);
    }
}
