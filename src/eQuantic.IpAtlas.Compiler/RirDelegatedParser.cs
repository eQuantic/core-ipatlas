using System.Net;
using System.Net.Sockets;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Parses the RIR "delegated-extended" statistics files — the five registries'
/// own public record of which address blocks they handed to which country.
/// Format per line: <c>registry|cc|type|start|value|date|status[|opaque]</c>,
/// where value is an address COUNT for ipv4 and a PREFIX LENGTH for ipv6.
/// Only allocated/assigned records with a real country code are yielded.
/// <para>
/// This says where a block was delegated, not where its addresses are used. It
/// is the floor the dataset stands on, and every other source outranks it.
/// </para>
/// </summary>
public static class RirDelegatedParser
{
    /// <summary>Yields the delegated ranges the file records.</summary>
    public static IEnumerable<AtlasEntry> Parse(TextReader reader) => Parse(reader, out _);

    /// <summary>Yields the delegated ranges, counting the lines it had to reject.</summary>
    public static IEnumerable<AtlasEntry> Parse(TextReader reader, out ParseCounters counters)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var tally = new ParseCounters();
        counters = tally;
        return Iterate(reader, tally);
    }

    private static IEnumerable<AtlasEntry> Iterate(TextReader reader, ParseCounters counters)
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
                continue; // version line, summary line, or malformed
            }

            var countryCode = fields[1];
            var type = fields[2];
            var status = fields[6];

            if (countryCode is "*" or "" || countryCode.Length != 2
                || status is not ("allocated" or "assigned"))
            {
                continue;
            }

            if (type == "ipv4")
            {
                if (!IPAddress.TryParse(fields[3], out var v4)
                    || v4.AddressFamily != AddressFamily.InterNetwork
                    || !uint.TryParse(fields[4], out var count) || count == 0)
                {
                    counters.Malformed++;
                    continue;
                }

                // The count is registry-supplied and unvalidated. A block that
                // would run past 255.255.255.255 is a bad record, not a range
                // to wrap around silently, so it is dropped and counted.
                var start = (ulong)AtlasEntry.ToNumber(v4, isV6: false);
                var end = start + count - 1;
                if (end > uint.MaxValue)
                {
                    counters.OutOfRange++;
                    continue;
                }

                yield return new AtlasEntry(false, start, end, countryCode);
            }
            else if (type == "ipv6")
            {
                if (!IPAddress.TryParse(fields[3], out var v6)
                    || v6.AddressFamily != AddressFamily.InterNetworkV6
                    || !int.TryParse(fields[4], out var prefix) || prefix is < 0 or > 128)
                {
                    counters.Malformed++;
                    continue;
                }

                var start = AtlasEntry.ToNumber(v6, isV6: true);
                var size = prefix == 0 ? UInt128.MaxValue : (UInt128.One << (128 - prefix)) - UInt128.One;
                yield return new AtlasEntry(true, start & ~size, (start & ~size) | size, countryCode);
            }
        }
    }
}

/// <summary>What a parser had to throw away, so a build can report it instead of hiding it.</summary>
public sealed class ParseCounters
{
    /// <summary>Lines whose fields did not parse.</summary>
    public int Malformed { get; set; }

    /// <summary>Records describing a range that cannot exist in its address family.</summary>
    public int OutOfRange { get; set; }

    /// <summary>Whether anything at all was rejected.</summary>
    public bool AnyRejected => Malformed > 0 || OutOfRange > 0;
}
