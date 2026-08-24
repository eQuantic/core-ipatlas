using System.Net;
using System.Net.Sockets;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Parses ip-to-ASN TSV data (the iptoasn.com combined layout):
/// <c>range_start\trange_end\tAS_number\tcountry\tAS_description</c>.
/// ASN 0 ("not routed") is skipped. Optional input — a dataset built without
/// it simply answers null for ASN.
/// <para>
/// The country column is deliberately ignored: it is itself derived from
/// registry delegations, so trusting it would launder the same error through a
/// second source and make the dataset look corroborated when it is not.
/// </para>
/// </summary>
public static class AsnTsvParser
{
    /// <summary>Yields the routed ranges the TSV describes.</summary>
    /// <param name="reader">The TSV to read.</param>
    /// <param name="classifyFromDescription">
    /// Whether to guess hosting, mobile and satellite flags from the AS name.
    /// Off by default: see <see cref="AsnHeuristics"/> for why a name match is
    /// not treated as evidence unless a build explicitly asks for it.
    /// </param>
    public static IEnumerable<AtlasEntry> Parse(TextReader reader, bool classifyFromDescription = false)
    {
        ArgumentNullException.ThrowIfNull(reader);

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
            var flags = classifyFromDescription && fields.Length > 4
                ? AsnHeuristics.Classify(fields[4])
                : NetworkTraits.None;

            yield return new AtlasEntry(
                isV6,
                AtlasEntry.ToNumber(startAddress, isV6),
                AtlasEntry.ToNumber(endAddress, isV6),
                Asn: asn,
                Traits: flags);
        }
    }
}
