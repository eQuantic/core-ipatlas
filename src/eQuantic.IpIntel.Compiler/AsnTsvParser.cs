using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace eQuantic.IpIntel.Compiler;

/// <summary>One routed range and the autonomous system announcing it.</summary>
public readonly record struct AsnRange(bool IsV6, UInt128 Start, UInt128 End, uint Asn);

/// <summary>
/// Parses ip-to-ASN TSV data (the iptoasn.com combined layout):
/// <c>range_start\trange_end\tAS_number\tcountry\tAS_description</c>.
/// ASN 0 ("not routed") is skipped. Optional input — a dataset built without
/// it simply answers null for ASN.
/// </summary>
public static class AsnTsvParser
{
    /// <summary>Yields the routed ranges the TSV describes.</summary>
    public static IEnumerable<AsnRange> Parse(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length < 3
                || !IPAddress.TryParse(fields[0], out var startAddress)
                || !IPAddress.TryParse(fields[1], out var endAddress)
                || !uint.TryParse(fields[2], out var asn)
                || asn == 0
                || startAddress.AddressFamily != endAddress.AddressFamily)
            {
                continue;
            }

            var isV6 = startAddress.AddressFamily == AddressFamily.InterNetworkV6;
            yield return new AsnRange(isV6, ToNumber(startAddress, isV6), ToNumber(endAddress, isV6), asn);
        }
    }

    private static UInt128 ToNumber(IPAddress address, bool isV6)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out var written);
        return isV6
            ? BinaryPrimitives.ReadUInt128BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes[..written]);
    }
}
