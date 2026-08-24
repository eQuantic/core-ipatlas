using System.IO.Compression;
using System.Net.Http;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>One file a build needs and where it comes from.</summary>
/// <param name="Name">The local file name.</param>
/// <param name="Url">Where to get it.</param>
/// <param name="Flag">Which build flag it feeds.</param>
/// <param name="Gzip">Whether the response is gzip that must be expanded.</param>
public readonly record struct SourceFile(string Name, string Url, string Flag, bool Gzip = false);

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
    ];

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
        foreach (var file in Catalogue)
        {
            var path = Path.Combine(directory, file.Name);
            try
            {
                var bytes = await DownloadAsync(client, file, path, attempts, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"  {file.Name,-24} {bytes,12:N0} bytes  {file.Flag}");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
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

        output.WriteLine();
        output.WriteLine("next:");
        output.WriteLine($"  eqatlas build --rir {directory}/delegated-* --asn {directory}/ip2asn.tsv \\");
        output.WriteLine($"    --cloud {directory}/aws-ranges.json {directory}/gcp-ranges.json \\");
        output.WriteLine($"    --anycast {directory}/cloudflare-v4.txt {directory}/cloudflare-v6.txt \\");
        output.WriteLine("    --out world.eqatlas");
        return 0;
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
