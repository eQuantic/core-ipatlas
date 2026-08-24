using System.Text.Json;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Pulls geofeed references out of one RDAP network response.
/// <para>
/// RDAP matters because two registries publish no bulk database this can read.
/// ARIN and LACNIC between them hold most of the Americas, and their operators'
/// geofeeds were written off as unreachable — wrongly. They are reachable, one
/// network object at a time.
/// </para>
/// <para>
/// Where the reference sits is not where RFC 9092 puts it. The RFC describes a
/// <c>geofeed</c> attribute on the network; ARIN carries it as free text in the
/// remarks of the <em>organisation entity</em> the network belongs to. So this
/// walks the whole document rather than reading one field, and takes the
/// network's own range as what the reference is authorised for — the
/// conservative reading, and the same rule the whois path applies.
/// </para>
/// </summary>
public static class RdapGeofeedReader
{
    /// <summary>What one RDAP network object said.</summary>
    /// <param name="Range">The network the response describes.</param>
    /// <param name="Urls">Geofeed URLs found anywhere in the document.</param>
    /// <param name="Organisation">The entity handle the network belongs to, for <c>--same-org</c>.</param>
    public readonly record struct Answer(AtlasEntry? Range, IReadOnlyList<string> Urls, string? Organisation);

    /// <summary>Reads one RDAP network response.</summary>
    public static Answer Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new Answer(null, [], null);
        }

        var urls = new List<string>();
        Collect(root, urls);

        return new Answer(ReadRange(root), urls, ReadOrganisation(root));
    }

    /// <summary>The network's range, from the CIDR list or the address bounds.</summary>
    private static AtlasEntry? ReadRange(JsonElement root)
    {
        if (root.TryGetProperty("cidr0_cidrs", out var cidrs) && cidrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var cidr in cidrs.EnumerateArray())
            {
                var prefix = Text(cidr, "v4prefix") ?? Text(cidr, "v6prefix");
                // TryGetInt32 is not the "try" its name promises: on an element
                // that is not a number it throws rather than answering false.
                // A registry that writes "length": "24" as a string used to take
                // the whole crawl down with it.
                if (prefix is not null
                    && cidr.ValueKind == JsonValueKind.Object
                    && cidr.TryGetProperty("length", out var length)
                    && Bits(length) is { } bits
                    && AtlasEntry.FromPrefix($"{prefix}/{bits}") is { } entry)
                {
                    return entry;
                }
            }
        }

        var start = Text(root, "startAddress");
        var end = Text(root, "endAddress");
        return start is not null && end is not null
            ? WhoisGeofeedIndex.ParseRange($"{start} - {end}")
            : null;
    }

    /// <summary>A prefix length written as either a number or a string.</summary>
    private static int? Bits(JsonElement length) => length.ValueKind switch
    {
        JsonValueKind.Number => length.TryGetInt32(out var number) ? number : null,
        JsonValueKind.String => int.TryParse(length.GetString(), out var text) ? text : null,
        _ => null,
    };

    /// <summary>
    /// The handle of the organisation the network belongs to, qualified by the
    /// registry that issued it. ARIN handles are short and unqualified — a bare
    /// "COGC" could collide with an unrelated handle elsewhere, and widening a
    /// feed's reach on a collision is exactly the mistake the authorisation
    /// check exists to prevent.
    /// </summary>
    private static string? ReadOrganisation(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entity in entities.EnumerateArray())
        {
            if (Text(entity, "handle") is { Length: > 0 } handle)
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>
    /// Every geofeed URL anywhere in the document. RDAP nests deeply and
    /// registries disagree about where to put this, so the whole tree is walked
    /// rather than a field read.
    /// </summary>
    private static void Collect(JsonElement node, List<string> urls)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    Collect(property.Value, urls);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Collect(item, urls);
                }

                break;

            case JsonValueKind.String:
                if (node.GetString() is { } text && text.Contains("geofeed", StringComparison.OrdinalIgnoreCase))
                {
                    Extract(text, urls);
                }

                break;

            default:
                break;
        }
    }

    private static void Extract(string text, List<string> urls)
    {
        var at = 0;
        while (at < text.Length)
        {
            var http = text.IndexOf("http", at, StringComparison.OrdinalIgnoreCase);
            if (http < 0)
            {
                return;
            }

            var end = http;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '"')
            {
                end++;
            }

            var candidate = text[http..end].TrimEnd('.', ',', ';', ')');
            if (candidate.Contains("geofeed", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !urls.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(uri.AbsoluteUri);
            }

            at = end;
        }
    }

    /// <summary>
    /// A string property, or null. Guards the element kind first: TryGetProperty
    /// throws on anything that is not an object, and RDAP responses come from
    /// five different implementations with their own ideas about types.
    /// </summary>
    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
