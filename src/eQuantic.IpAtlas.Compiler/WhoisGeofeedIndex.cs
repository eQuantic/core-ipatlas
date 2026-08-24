using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>One geofeed URL and the range whose registry object pointed at it.</summary>
/// <param name="Range">The inetnum or inet6num that carried the reference.</param>
/// <param name="Url">Where the operator publishes its geofeed.</param>
/// <param name="Organisation">
/// The <c>org:</c> handle on that object, when it has one. This is what lets a
/// feed be trusted for more than the objects that named it, without trusting
/// the feed itself: see <see cref="GeofeedAuthorization"/>.
/// </param>
public readonly record struct GeofeedReference(AtlasEntry Range, string Url, string? Organisation);

/// <summary>
/// Finds geofeed URLs in a registry's bulk whois dump, per RFC 9092.
/// <para>
/// An operator says where its own addresses are by publishing an RFC 8805 file
/// and pointing at it from the registry object for the block. That pointer is
/// the whole mechanism: it is what ties a URL anyone could host to a range the
/// publisher demonstrably holds. The range travels with the URL here for
/// exactly that reason — see <see cref="GeofeedAuthorization"/>, which is where
/// it is enforced.
/// </para>
/// Both the modern <c>geofeed:</c> attribute and the older
/// <c>remarks: Geofeed &lt;url&gt;</c> convention are read.
/// </summary>
public static class WhoisGeofeedIndex
{
    /// <summary>Reads a dump from a file, transparently expanding gzip.</summary>
    public static IEnumerable<GeofeedReference> ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(OpenText(path));
    }

    /// <summary>Reads every geofeed reference in a whois dump.</summary>
    public static IEnumerable<GeofeedReference> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Iterate(reader);
    }

    private static IEnumerable<GeofeedReference> Iterate(TextReader reader)
    {
        using (reader)
        {
            AtlasEntry? range = null;
            string? organisation = null;
            var urls = new List<string>();

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    foreach (var reference in Emit(range, organisation, urls))
                    {
                        yield return reference;
                    }

                    range = null;
                    organisation = null;
                    urls.Clear();
                    continue;
                }

                if (line[0] is '#' or '%' or ' ' or '\t' or '+')
                {
                    continue; // comment, or the continuation of an attribute we do not read
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                {
                    continue;
                }

                var name = line.AsSpan(0, colon);
                var value = line.AsSpan(colon + 1).Trim();
                if (value.IsEmpty)
                {
                    continue;
                }

                if (name.Equals("inetnum", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("inet6num", StringComparison.OrdinalIgnoreCase))
                {
                    range = ParseRange(value.ToString());
                }
                else if (name.Equals("org", StringComparison.OrdinalIgnoreCase))
                {
                    organisation = value.ToString();
                }
                else if (name.Equals("geofeed", StringComparison.OrdinalIgnoreCase))
                {
                    if (Url(value.ToString()) is { } url)
                    {
                        urls.Add(url);
                    }
                }
                else if (name.Equals("remarks", StringComparison.OrdinalIgnoreCase)
                    && value.StartsWith("geofeed", StringComparison.OrdinalIgnoreCase)
                    && Url(value[7..].Trim().ToString()) is { } remarked)
                {
                    urls.Add(remarked);
                }
            }

            foreach (var reference in Emit(range, organisation, urls))
            {
                yield return reference;
            }
        }
    }

    /// <summary>
    /// Every range a registry object records against one of the given
    /// organisations, ignoring geofeeds entirely.
    /// <para>
    /// This is the second pass that <c>--same-org</c> needs. It is separate
    /// because the set of organisations worth looking for is only known after
    /// the first pass, and holding every inetnum in a registry would cost far
    /// more memory than holding the few thousand that belong to publishers.
    /// </para>
    /// </summary>
    /// <param name="path">A registry database dump.</param>
    /// <param name="organisations">The handles to collect ranges for.</param>
    public static IEnumerable<(AtlasEntry Range, string Organisation)> ParseOrganisationRanges(
        string path, IReadOnlySet<string> organisations)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(organisations);
        return organisations.Count == 0 ? [] : IterateOrganisations(OpenText(path), organisations);
    }

    private static IEnumerable<(AtlasEntry Range, string Organisation)> IterateOrganisations(
        TextReader reader, IReadOnlySet<string> organisations)
    {
        using (reader)
        {
            AtlasEntry? range = null;
            string? organisation = null;

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    if (range is { } value && organisation is { } handle && organisations.Contains(handle))
                    {
                        yield return (value, handle);
                    }

                    range = null;
                    organisation = null;
                    continue;
                }

                if (line[0] is '#' or '%' or ' ' or '\t' or '+')
                {
                    continue;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                {
                    continue;
                }

                var name = line.AsSpan(0, colon);
                var value2 = line.AsSpan(colon + 1).Trim();
                if (value2.IsEmpty)
                {
                    continue;
                }

                if (name.Equals("inetnum", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("inet6num", StringComparison.OrdinalIgnoreCase))
                {
                    range = ParseRange(value2.ToString());
                }
                else if (name.Equals("org", StringComparison.OrdinalIgnoreCase))
                {
                    organisation = value2.ToString();
                }
            }

            if (range is { } last && organisation is { } lastHandle && organisations.Contains(lastHandle))
            {
                yield return (last, lastHandle);
            }
        }
    }

    private static IEnumerable<GeofeedReference> Emit(AtlasEntry? range, string? organisation, List<string> urls)
    {
        if (range is not { } value)
        {
            yield break;
        }

        foreach (var url in urls)
        {
            yield return new GeofeedReference(value, url, organisation);
        }
    }

    /// <summary>The URL out of an attribute value, or null when there is not one.</summary>
    private static string? Url(string value)
    {
        var text = value.Trim().TrimEnd('.', ',', ';', ')');
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            text = text[..space];
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>
    /// Reads the two shapes registries write ranges in: a CIDR, or "start - end".
    /// Returns null when the value is neither.
    /// </summary>
    public static AtlasEntry? ParseRange(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return AtlasEntry.FromPrefix(value.Trim());
        }

        if (!IPAddress.TryParse(value.AsSpan(0, dash).Trim(), out var first)
            || !IPAddress.TryParse(value.AsSpan(dash + 1).Trim(), out var last)
            || first.AddressFamily != last.AddressFamily)
        {
            return null;
        }

        var isV6 = first.AddressFamily == AddressFamily.InterNetworkV6;
        var start = AtlasEntry.ToNumber(first, isV6);
        var end = AtlasEntry.ToNumber(last, isV6);
        return start <= end ? new AtlasEntry(isV6, start, end) : null;
    }

    private static StreamReader OpenText(string path)
    {
        var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[2];
        var read = stream.ReadAtLeast(magic, 2, throwOnEndOfStream: false);
        stream.Position = 0;

        // Registries publish these gzipped, and people gunzip them. Sniff the
        // magic instead of trusting the extension.
        return read == 2 && magic[0] == 0x1F && magic[1] == 0x8B
            ? new StreamReader(new GZipStream(stream, CompressionMode.Decompress))
            : new StreamReader(stream);
    }
}
