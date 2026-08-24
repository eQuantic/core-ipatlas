namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Guesses what kind of network an autonomous system is from its name.
/// <para>
/// This is a heuristic and it is treated as one: it is off unless a build asks
/// for it, because a name match is not evidence. "Cloud" appears in the names
/// of plenty of residential ISPs, and a datacenter that calls itself nothing in
/// particular goes unflagged. Cloud provider range files are the measured
/// source and always outrank this; these tokens only reach networks no
/// published range file covers.
/// </para>
/// </summary>
public static class AsnHeuristics
{
    private static readonly string[] HostingTokens =
    [
        "hosting", "datacenter", "data center", "data-center", "dedicated server",
        "colocation", "colo ", "vps", "virtual server", "webhost", "web host", "server farm",
    ];

    private static readonly string[] MobileTokens =
    [
        " mobile", "wireless", "cellular", "gsm ", " lte", "telecom mobile",
    ];

    private static readonly string[] SatelliteTokens =
    [
        "satellite", "starlink", "viasat", "hughesnet",
    ];

    /// <summary>What an AS description suggests about the network, or none when nothing matches.</summary>
    public static IpFlags Classify(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return IpFlags.None;
        }

        var text = description.ToLowerInvariant();
        var flags = IpFlags.None;
        if (ContainsAny(text, HostingTokens))
        {
            flags |= IpFlags.Hosting;
        }

        if (ContainsAny(text, MobileTokens))
        {
            flags |= IpFlags.Mobile;
        }

        if (ContainsAny(text, SatelliteTokens))
        {
            flags |= IpFlags.Satellite;
        }

        return flags;
    }

    private static bool ContainsAny(string text, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
