using System.Buffers.Binary;
using System.Net;

namespace eQuantic.IpIntel.Compiler;

/// <summary>One delegated range with the country the registry recorded for it.</summary>
public readonly record struct CountryRange(bool IsV6, UInt128 Start, UInt128 End, string CountryCode);

/// <summary>
/// Parses the RIR "delegated-extended" statistics files — the five registries'
/// own public record of which address blocks they handed to which country.
/// Format per line: <c>registry|cc|type|start|value|date|status[|opaque]</c>,
/// where value is an address COUNT for ipv4 and a PREFIX LENGTH for ipv6.
/// Only allocated/assigned records with a real country code are yielded.
/// </summary>
public static class RirDelegatedParser
{
    /// <summary>Yields the delegated ranges the file records.</summary>
    public static IEnumerable<CountryRange> Parse(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length < 7)
            {
                continue; // version line or malformed
            }

            var countryCode = fields[1];
            var type = fields[2];
            var status = fields[6];

            if (countryCode is "*" or "" || countryCode.Length != 2
                || status is not ("allocated" or "assigned"))
            {
                continue;
            }

            if (type == "ipv4"
                && IPAddress.TryParse(fields[3], out var v4)
                && v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && uint.TryParse(fields[4], out var count) && count > 0)
            {
                Span<byte> bytes = stackalloc byte[4];
                v4.TryWriteBytes(bytes, out _);
                var start = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                yield return new CountryRange(false, start, start + (ulong)count - 1, countryCode);
            }
            else if (type == "ipv6"
                && IPAddress.TryParse(fields[3], out var v6)
                && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && int.TryParse(fields[4], out var prefix) && prefix is >= 1 and <= 128)
            {
                Span<byte> bytes = stackalloc byte[16];
                v6.TryWriteBytes(bytes, out _);
                var start = BinaryPrimitives.ReadUInt128BigEndian(bytes);
                var size = prefix == 128 ? UInt128.One : UInt128.One << (128 - prefix);
                yield return new CountryRange(true, start, start + size - 1, countryCode);
            }
        }
    }
}
