namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Reads a plain list of prefixes or addresses, one per line, and stamps the
/// traits the caller names on every one. This is the shape several publishers
/// use for the lists that say what a network is rather than where it is:
/// Cloudflare's anycast prefixes, the Tor Project's exit nodes.
/// </summary>
public static class PrefixListParser
{
    /// <summary>Yields one entry per prefix or address in the list.</summary>
    public static IEnumerable<AtlasEntry> Parse(TextReader reader, NetworkTraits traits)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] is '#' or ';')
            {
                continue;
            }

            if (AtlasEntry.FromPrefix(text, traits: traits) is { } entry)
            {
                yield return entry;
            }
        }
    }
}
