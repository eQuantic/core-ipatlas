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
    public static NetworkTraits Classify(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return NetworkTraits.None;
        }

        var text = description.ToLowerInvariant();
        var flags = NetworkTraits.None;
        if (ContainsAny(text, HostingTokens))
        {
            flags |= NetworkTraits.Hosting;
        }

        if (ContainsAny(text, MobileTokens))
        {
            flags |= NetworkTraits.Mobile;
        }

        if (ContainsAny(text, SatelliteTokens))
        {
            flags |= NetworkTraits.Satellite;
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
