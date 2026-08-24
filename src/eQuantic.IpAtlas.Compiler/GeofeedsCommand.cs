using System.Globalization;
using System.Net.Http;
using System.Text;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>What a geofeed harvest did, so a build can report it rather than imply it.</summary>
/// <param name="References">Geofeed pointers found in the registry dumps.</param>
/// <param name="Feeds">Distinct URLs behind them.</param>
/// <param name="Fetched">Feeds that answered.</param>
/// <param name="Unreadable">Feeds that answered with something that is not a geofeed.</param>
/// <param name="Failed">Feeds that could not be reached.</param>
/// <param name="Accepted">Prefixes kept.</param>
/// <param name="Unauthorized">Prefixes a feed had no standing to describe.</param>
/// <param name="WorstOffenders">
/// The feeds that claimed the most ground they had no registry object for,
/// worst first. Naming them is the point: a feed rejecting nothing and a feed
/// rejecting a million prefixes are very different facts, and an aggregate
/// hides which one you have.
/// </param>
public readonly record struct HarvestReport(
    int References, int Feeds, int Fetched, int Unreadable, int Failed, int Accepted, int Unauthorized,
    IReadOnlyList<(string Url, int Rejected, int Kept)> WorstOffenders);

/// <summary>
/// Collects the geofeeds operators publish about their own networks and writes
/// them out as one RFC 8805 file a build can consume.
/// <para>
/// This is the only free path to city-level accuracy outside the cloud
/// providers' own ranges, and it is a crawl: thousands of small files on
/// thousands of hosts, each authoritative for a little of the internet and
/// nothing else. What arrives is treated as what it is — untrusted input from
/// the open web. A response that does not parse as a geofeed is dropped, and
/// every prefix is checked against the registry objects that pointed at the
/// feed before a single one is written.
/// </para>
/// </summary>
public static class GeofeedsCommand
{
    /// <summary>Harvests every geofeed the given registry dumps point at.</summary>
    public static async Task<int> RunAsync(
        Arguments args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var dumps = args.ExistingFiles("whois");
        var outPath = args.One("out", required: true);
        var concurrency = Number(args, "concurrency", 16, 1, 64);
        var timeout = Number(args, "timeout", 15, 1, 120);
        var limit = Number(args, "limit", int.MaxValue, 1, int.MaxValue);
        var attempts = Number(args, "attempts", 3, 1, 10);

        if (dumps.Count == 0)
        {
            args.Fail("--whois needs at least one registry database dump");
        }

        if (args.Errors.Count > 0)
        {
            foreach (var message in args.Errors)
            {
                error.WriteLine($"eqatlas: {message}");
            }

            return 2;
        }

        var index = new Dictionary<string, GeofeedAuthorization>(StringComparer.Ordinal);
        var references = 0;
        foreach (var dump in dumps)
        {
            var found = 0;
            foreach (var reference in WhoisGeofeedIndex.ParseFile(dump))
            {
                if (!index.TryGetValue(reference.Url, out var authorization))
                {
                    index[reference.Url] = authorization = new GeofeedAuthorization();
                }

                authorization.Allow(reference.Range);
                found++;
            }

            references += found;
            output.WriteLine($"  {Path.GetFileName(dump),-32} {found,8:N0} geofeed references");
        }

        if (index.Count == 0)
        {
            error.WriteLine("eqatlas: no geofeed references in those dumps");
            return 1;
        }

        foreach (var authorization in index.Values)
        {
            authorization.Compact();
        }

        var feeds = index.AsEnumerable();
        if (limit < index.Count)
        {
            // Ordered, so a bounded run is reproducible rather than a random sample.
            feeds = index.OrderBy(entry => entry.Key, StringComparer.Ordinal).Take(limit);
            output.WriteLine();
            output.WriteLine($"  --limit {limit}: fetching {limit:N0} of {index.Count:N0} feeds");
        }

        var planned = Math.Min(limit, index.Count);
        output.WriteLine();
        output.WriteLine($"  {planned:N0} distinct feeds to fetch, {concurrency} at a time");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("eQuantic.IpAtlas.Compiler");
        client.MaxResponseContentBufferSize = 8 * 1024 * 1024;

        var results = new System.Collections.Concurrent.ConcurrentBag<List<AtlasEntry>>();
        var offenders = new System.Collections.Concurrent.ConcurrentBag<(string Url, int Rejected, int Kept)>();
        int fetched = 0, unreadable = 0, failed = 0, unauthorized = 0;

        await Parallel.ForEachAsync(
            feeds,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (feed, token) =>
            {
                var body = await ReadAsync(client, feed.Key, attempts, token).ConfigureAwait(false);
                if (body is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                var kept = new List<AtlasEntry>();
                var rejected = 0;
                var parsed = 0;
                using (var reader = new StringReader(body))
                {
                    foreach (var entry in GeofeedParser.Parse(reader))
                    {
                        parsed++;
                        if (feed.Value.Covers(entry))
                        {
                            kept.Add(entry);
                        }
                        else
                        {
                            rejected++;
                        }
                    }
                }

                if (parsed == 0)
                {
                    Interlocked.Increment(ref unreadable);
                    return;
                }

                Interlocked.Increment(ref fetched);
                Interlocked.Add(ref unauthorized, rejected);
                if (rejected > 0)
                {
                    offenders.Add((feed.Key, rejected, kept.Count));
                }

                if (kept.Count > 0)
                {
                    results.Add(kept);
                }
            }).ConfigureAwait(false);

        var accepted = new List<AtlasEntry>();
        foreach (var batch in results)
        {
            accepted.AddRange(batch);
        }

        accepted.Sort((left, right) =>
        {
            var family = left.IsV6.CompareTo(right.IsV6);
            return family != 0 ? family : left.Start.CompareTo(right.Start);
        });

        await WriteAsync(outPath!, accepted, cancellationToken).ConfigureAwait(false);

        var report = new HarvestReport(
            references, planned, fetched, unreadable, failed, accepted.Count, unauthorized,
            offenders.OrderByDescending(entry => entry.Rejected).Take(5).ToList());
        Report(output, report, outPath!);
        return accepted.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Fetches every feed and keeps only what each is authorised to claim.
    /// <para>
    /// Separate from the command, and taking the fetch as a delegate, so the
    /// check that decides whose claims are believed can be tested against
    /// hostile inputs rather than only against whatever the internet happened
    /// to serve that day.
    /// </para>
    /// </summary>
    /// <param name="feeds">Each URL with the ranges whose registry objects referenced it.</param>
    /// <param name="fetch">How to read one feed, answering null when it cannot be read.</param>
    /// <param name="concurrency">How many feeds to read at once.</param>
    /// <param name="cancellationToken">Cancels the harvest.</param>
    public static async Task<(List<AtlasEntry> Accepted, HarvestReport Report)> HarvestAsync(
        IEnumerable<KeyValuePair<string, GeofeedAuthorization>> feeds,
        Func<string, CancellationToken, Task<string?>> fetch,
        int concurrency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(fetch);

        var results = new System.Collections.Concurrent.ConcurrentBag<List<AtlasEntry>>();
        var offenders = new System.Collections.Concurrent.ConcurrentBag<(string Url, int Rejected, int Kept)>();
        int fetched = 0, unreadable = 0, failed = 0, unauthorized = 0, feedCount = 0;

        await Parallel.ForEachAsync(
            feeds,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (feed, token) =>
            {
                Interlocked.Increment(ref feedCount);
                var body = await fetch(feed.Key, token).ConfigureAwait(false);
                if (body is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                var kept = new List<AtlasEntry>();
                var rejected = 0;
                var parsed = 0;
                using (var reader = new StringReader(body))
                {
                    foreach (var entry in GeofeedParser.Parse(reader))
                    {
                        parsed++;
                        if (feed.Value.Covers(entry))
                        {
                            kept.Add(entry);
                        }
                        else
                        {
                            rejected++;
                        }
                    }
                }

                if (parsed == 0)
                {
                    Interlocked.Increment(ref unreadable);
                    return;
                }

                Interlocked.Increment(ref fetched);
                Interlocked.Add(ref unauthorized, rejected);
                if (rejected > 0)
                {
                    offenders.Add((feed.Key, rejected, kept.Count));
                }

                if (kept.Count > 0)
                {
                    results.Add(kept);
                }
            }).ConfigureAwait(false);

        var accepted = new List<AtlasEntry>();
        foreach (var batch in results)
        {
            accepted.AddRange(batch);
        }

        accepted.Sort((left, right) =>
        {
            var family = left.IsV6.CompareTo(right.IsV6);
            return family != 0 ? family : left.Start.CompareTo(right.Start);
        });

        return (accepted, new HarvestReport(
            0, feedCount, fetched, unreadable, failed, accepted.Count, unauthorized,
            offenders.OrderByDescending(entry => entry.Rejected).Take(5).ToList()));
    }

    private static void Report(TextWriter output, HarvestReport report, string outPath)
    {
        output.WriteLine();
        output.WriteLine($"  {report.Fetched,8:N0} feeds read");
        output.WriteLine($"  {report.Unreadable,8:N0} answered with something that is not a geofeed");
        output.WriteLine($"  {report.Failed,8:N0} could not be reached");
        output.WriteLine();
        output.WriteLine($"  {report.Accepted,8:N0} prefixes accepted");
        output.WriteLine($"  {report.Unauthorized,8:N0} prefixes discarded: the feed had no registry object for them");

        if (report.WorstOffenders.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  claimed the most ground they do not hold:");
            foreach (var (url, rejected, kept) in report.WorstOffenders)
            {
                output.WriteLine($"  {rejected,8:N0} discarded, {kept:N0} kept  {Shorten(url)}");
            }
        }

        output.WriteLine();
        output.WriteLine($"wrote {outPath}");
    }

    /// <summary>
    /// Fetches one feed, retrying with a widening delay. A crawl of thousands of
    /// small servers has a long tail of transient failures, and treating the
    /// first timeout as a verdict throws away real data.
    /// </summary>
    private static async Task<string?> ReadAsync(
        HttpClient client, string url, int attempts, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or UriFormatException)
            {
                if (attempt == attempts || IsFinal(ex))
                {
                    return null;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a failure is worth asking about again. A refusal is an answer, and
    /// a hostname that does not resolve will not resolve on the third try either.
    /// Across five thousand feeds the dead ones are a large minority, and
    /// retrying them is most of the wall clock for none of the data.
    /// </summary>
    private static bool IsFinal(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500)
        {
            return true;
        }

        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Net.Sockets.SocketException socket)
            {
                return socket.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                    or System.Net.Sockets.SocketError.NoData
                    or System.Net.Sockets.SocketError.ConnectionRefused
                    or System.Net.Sockets.SocketError.NetworkUnreachable;
            }
        }

        return false;
    }

    private static string Shorten(string url) => url.Length <= 72 ? url : url[..69] + "...";

    private static async Task WriteAsync(string path, List<AtlasEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var temporary = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");

        var text = new StringBuilder();
        text.AppendLine("# Harvested from registry geofeed references (RFC 9092).");
        text.AppendLine("# Every prefix here was published by the holder of the registry object");
        text.AppendLine("# that pointed at its feed; anything a feed claimed beyond that was dropped.");
        foreach (var entry in entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"{entry.ToCidr()},{entry.CountryCode}");
            text.Append(CultureInfo.InvariantCulture, $",{entry.Region},{entry.City}");
            text.AppendLine();
        }

        await File.WriteAllTextAsync(temporary, text.ToString(), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static int Number(Arguments args, string name, int fallback, int min, int max)
    {
        if (args.One(name) is not { } text)
        {
            return fallback;
        }

        if (!int.TryParse(text, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            args.Fail($"--{name} '{text}' must be between {min} and {max}");
            return fallback;
        }

        return value;
    }
}
