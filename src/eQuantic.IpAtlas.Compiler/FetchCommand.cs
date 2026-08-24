using System.IO.Compression;
using System.Net.Http;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>One file a build needs and where it comes from.</summary>
/// <param name="Name">The local file name.</param>
/// <param name="Url">Where to get it.</param>
/// <param name="Flag">Which build flag it feeds.</param>
/// <param name="Gzip">Whether the response is gzip that must be expanded.</param>
/// <param name="Optional">
/// Whether a failure here is a warning rather than a failed fetch. Reserved for
/// sources published behind a page rather than at a fixed URL: worth having,
/// never worth making a scheduled rebuild depend on.
/// </param>
/// <param name="DiscoverFrom">A page to search for the real URL, when there is no fixed one.</param>
public readonly record struct SourceFile(
    string Name, string Url, string Flag, bool Gzip = false, bool Optional = false, string? DiscoverFrom = null);

/// <summary>
/// Downloads the public source files a dataset is built from.
/// <para>
/// Every URL here is a registry's or an operator's own published record, free
/// to use and free of licence encumbrance — which is the whole premise of this
/// library, and worth keeping in one auditable list rather than scattered
/// across a README someone copies by hand. Downloads land in a temporary file
/// and are renamed into place, so an interrupted fetch cannot leave a
/// half-written source behind for the next build to read as truth.
/// </para>
/// </summary>
public static class FetchCommand
{
    /// <summary>The sources a full world dataset is built from.</summary>
    public static IReadOnlyList<SourceFile> Catalogue { get; } =
    [
        new("delegated-afrinic", "https://ftp.afrinic.net/pub/stats/afrinic/delegated-afrinic-extended-latest", "--rir"),
        new("delegated-apnic", "https://ftp.apnic.net/stats/apnic/delegated-apnic-extended-latest", "--rir"),
        new("delegated-arin", "https://ftp.arin.net/pub/stats/arin/delegated-arin-extended-latest", "--rir"),
        new("delegated-lacnic", "https://ftp.lacnic.net/pub/stats/lacnic/delegated-lacnic-extended-latest", "--rir"),
        new("delegated-ripencc", "https://ftp.ripe.net/pub/stats/ripencc/delegated-ripencc-extended-latest", "--rir"),
        new("ip2asn.tsv", "https://iptoasn.com/data/ip2asn-combined.tsv.gz", "--asn", Gzip: true),
        new("aws-ranges.json", "https://ip-ranges.amazonaws.com/ip-ranges.json", "--cloud"),
        new("gcp-ranges.json", "https://www.gstatic.com/ipranges/cloud.json", "--cloud"),
        new("cloudflare-v4.txt", "https://www.cloudflare.com/ips-v4", "--anycast"),
        new("cloudflare-v6.txt", "https://www.cloudflare.com/ips-v6", "--anycast"),

        // The one anonymizer source that is both free and authoritative: the Tor
        // Project's own list of exit nodes. It covers Tor and nothing else, which
        // is a small slice of the anonymizer problem, but a slice measured rather
        // than guessed.
        new("tor-exits.txt", "https://check.torproject.org/torbulkexitlist", "--anonymizer"),

        // Azure publishes the same kind of data as AWS and Google, but behind a
        // download page with a dated file name rather than at a fixed URL. It is
        // worth having: without it, Azure address space keeps answering with the
        // country Microsoft registered it in. It is not worth failing a nightly
        // rebuild over, so discovery failures are a warning and the fetch goes on.
        new("azure-ranges.json", string.Empty, "--cloud", Optional: true,
            DiscoverFrom: "https://www.microsoft.com/en-us/download/details.aspx?id=56519"),
    ];

    private static readonly System.Text.RegularExpressions.Regex AzureLink = new(
        @"https://download\.microsoft\.com/download/[^""]*?ServiceTags_Public_\d+\.json",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    /// <summary>Downloads every source into a directory.</summary>
    public static async Task<int> RunAsync(
        Arguments args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var directory = args.One("into") ?? ".";
        var attempts = 3;
        if (args.One("attempts") is { } text && (!int.TryParse(text, out attempts) || attempts < 1))
        {
            args.Fail($"--attempts '{text}' is not a positive number");
        }

        if (args.Errors.Count > 0)
        {
            foreach (var message in args.Errors)
            {
                error.WriteLine($"eqatlas: {message}");
            }

            return 2;
        }

        Directory.CreateDirectory(directory);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("eQuantic.IpAtlas.Compiler");

        var failures = 0;
        var optionalSkipped = new List<SourceFile>();
        foreach (var file in Catalogue)
        {
            var path = Path.Combine(directory, file.Name);
            try
            {
                var resolved = file;
                if (file.DiscoverFrom is { } page)
                {
                    resolved = file with { Url = await DiscoverAsync(client, page, cancellationToken).ConfigureAwait(false) };
                }

                var bytes = await DownloadAsync(client, resolved, path, attempts, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"  {file.Name,-24} {bytes,12:N0} bytes  {file.Flag}");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
            {
                if (file.Optional)
                {
                    output.WriteLine($"  {file.Name,-24} {"skipped",12}  {ex.Message}");
                    optionalSkipped.Add(file);
                    continue;
                }

                error.WriteLine($"  {file.Name,-24} FAILED  {ex.Message}");
                failures++;
            }
        }

        if (failures > 0)
        {
            error.WriteLine();
            error.WriteLine($"eqatlas: {failures} of {Catalogue.Count} sources failed; not building from a partial set");
            return 1;
        }

        if (optionalSkipped.Count > 0)
        {
            output.WriteLine();
            foreach (var file in optionalSkipped)
            {
                output.WriteLine(
                    $"  {file.Name} could not be discovered. Download it by hand from");
                output.WriteLine($"  {file.DiscoverFrom} and pass it with {file.Flag}.");
                output.WriteLine("  Without it, that provider's addresses answer with the country it registered them in.");
            }
        }

        output.WriteLine();
        output.WriteLine("next:");
        output.WriteLine($"  eqatlas build --rir {directory}/delegated-* --asn {directory}/ip2asn.tsv \\");
        output.WriteLine($"    --cloud {directory}/*-ranges.json \\");
        output.WriteLine($"    --anycast {directory}/cloudflare-v4.txt {directory}/cloudflare-v6.txt \\");
        output.WriteLine($"    --anonymizer {directory}/tor-exits.txt \\");
        output.WriteLine("    --out world.eqatlas");
        return 0;
    }

    /// <summary>Finds the real URL on a page that publishes it under a changing name.</summary>
    private static async Task<string> DiscoverAsync(HttpClient client, string page, CancellationToken cancellationToken)
    {
        var html = await client.GetStringAsync(page, cancellationToken).ConfigureAwait(false);
        var match = AzureLink.Match(html);
        return match.Success
            ? match.Value
            : throw new InvalidOperationException("no download link on the publisher's page");
    }

    private static async Task<long> DownloadAsync(
        HttpClient client, SourceFile file, string path, int attempts, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var response = await client
                    .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var target = File.Create(temporary);
                    if (file.Gzip)
                    {
                        await using var expand = new GZipStream(body, CompressionMode.Decompress);
                        await expand.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await body.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                }

                var length = new FileInfo(temporary).Length;
                if (length == 0)
                {
                    throw new IOException("the server returned an empty file");
                }

                File.Move(temporary, path, overwrite: true);
                return length;
            }
            catch (Exception ex) when (attempt < attempts && ex is HttpRequestException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is not worth masking the real failure.
        }
    }
}
