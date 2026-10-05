using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Discovers geofeed references by asking a registry about every block it
/// delegated, one at a time.
/// <para>
/// Two registries publish no bulk database this can read, which left the
/// Americas out of the geofeed harvest entirely. RDAP is how you reach them:
/// slower than a database dump by four orders of magnitude, but it is a public,
/// stable, machine-readable interface that every registry runs, and the input
/// list is already in hand — the delegated files say exactly which blocks
/// exist.
/// </para>
/// <para>
/// A run records every block it asked about, including the ones with no
/// geofeed, so the output doubles as an audit of what was checked and lets a
/// later run resume instead of starting over. At a hundred thousand queries
/// that is not a convenience, it is the difference between a job that finishes
/// and one that has to.
/// </para>
/// </summary>
public static class RdapCommand
{
    /// <summary>Where each registry answers RDAP queries.</summary>
    private static readonly Dictionary<string, string> Endpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arin"] = "https://rdap.arin.net/registry/ip/",
        ["lacnic"] = "https://rdap.lacnic.net/rdap/ip/",
        ["ripencc"] = "https://rdap.db.ripe.net/ip/",
        ["apnic"] = "https://rdap.apnic.net/ip/",
        ["afrinic"] = "https://rdap.afrinic.net/rdap/ip/",
    };

    /// <summary>
    /// National registries that answer better than their regional one for the
    /// country they serve.
    /// <para>
    /// This is not a shortcut, it is the only route to some of the data. Asked
    /// about a Brazilian block, LACNIC returns a rate-limit page; Registro.br
    /// returns twelve kilobytes including the operator's geofeed. Brazil is 65%
    /// of LACNIC's delegations, so sending those queries where they are answered
    /// also takes two thirds of the load off a registry that plainly cannot
    /// carry it.
    /// </para>
    /// <para>
    /// Keyed by registry and the country code the delegated file records, which
    /// is the only routing signal available before a query is made. Other
    /// national registries advertise RDAP endpoints that did not answer when
    /// tested, so they are not listed on the strength of existing.
    /// </para>
    /// </summary>
    private static readonly Dictionary<(string Registry, string Country), (string Name, string Endpoint)> National =
        new()
        {
            [("lacnic", "BR")] = ("registro.br", "https://rdap.registro.br/ip/"),
        };

    /// <summary>Asks a registry about each of its delegations and records what it says.</summary>
    public static async Task<int> RunAsync(
        Arguments args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var delegated = args.ExistingFiles("delegated");
        var outPath = args.One("out", required: true);
        // Measured against ARIN: 8 gives 44 req/s, 16 gives 80, and 24 drops
        // back to 57 because the registry starts throttling. The default is the
        // gentle one rather than the fast one — this is someone else's public
        // service, and for a monthly job the difference is 46 minutes against
        // 25, which matters to nobody.
        var concurrency = Number(args, "concurrency", 8, 1, 32);
        var timeout = Number(args, "timeout", 20, 1, 120);
        var attempts = Number(args, "attempts", 3, 1, 10);
        var limit = Number(args, "limit", int.MaxValue, 1, int.MaxValue);
        var perHost = Number(args, "per-host", 4, 1, 16);
        var cache = args.One("cache");

        if (delegated.Count == 0)
        {
            args.Fail("--delegated needs at least one registry delegation file");
        }

        if (args.Errors.Count > 0)
        {
            foreach (var message in args.Errors)
            {
                error.WriteLine($"eqatlas: {message}");
            }

            return 2;
        }

        var work = new List<Delegation>();
        foreach (var file in delegated)
        {
            var before = work.Count;
            work.AddRange(Read(file, error));
            output.WriteLine($"  {Path.GetFileName(file),-28} {work.Count - before,8:N0} delegations");
        }

        if (work.Count == 0)
        {
            error.WriteLine("eqatlas: no delegations from a registry this knows an RDAP endpoint for");
            return 1;
        }

        var already = Resume(outPath!, output);
        var pending = work.Where(item => !already.Contains(item.Key)).Take(limit).ToList();

        output.WriteLine();
        output.WriteLine($"  {pending.Count:N0} to ask about, {concurrency} at a time"
            + (already.Count > 0 ? $" ({already.Count:N0} already recorded)" : string.Empty));

        if (pending.Count == 0)
        {
            output.WriteLine("  nothing left to do");
            return 0;
        }

        // The same fetcher the geofeed harvest uses, for the same reason it was
        // written: a global concurrency number says nothing about how hard any
        // one server is being pushed. Every query in this crawl goes to a
        // handful of hosts, so the limit that matters is per host — and LACNIC
        // starts answering 429 well before ARIN notices anything.
        using var fetcher = new PoliteFetcher(
            TimeSpan.FromSeconds(timeout), attempts, perHost, cache, "application/rdap+json");

        // Written as results arrive, not at the end. A crawl of this size takes
        // the better part of an hour, and holding every line in memory until it
        // finishes means an interrupted run leaves nothing behind — which is the
        // exact failure the resume file exists to prevent.
        await using var sink = new ResultSink(outPath!, already.Count == 0);
        int answered = 0, failed = 0, references = 0;

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var body = await fetcher.GetAsync(item.Url, token).ConfigureAwait(false);
                if (body is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                Interlocked.Increment(ref answered);

                RdapGeofeedReader.Answer answer;
                try
                {
                    answer = RdapGeofeedReader.Read(body);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Parsing a stranger's JSON must never take the crawl down.
                    // Catching only JsonException was not enough: a registry
                    // typed a field differently and the escaping exception cost
                    // forty-six minutes of work.
                    Interlocked.Increment(ref failed);
                    return;
                }

                var range = answer.Range ?? item.Range;
                var organisation = answer.Organisation is { Length: > 0 } handle
                    ? $"{item.Registry}:{handle}"
                    : string.Empty;

                if (answer.Urls.Count == 0)
                {
                    // Recorded anyway: this is what makes a resume possible and
                    // what makes the file an account of everything checked.
                    await sink.WriteAsync($"{item.Key},{range.ToCidr()},,{organisation}", token).ConfigureAwait(false);
                    return;
                }

                Interlocked.Add(ref references, answer.Urls.Count);
                foreach (var url in answer.Urls)
                {
                    await sink.WriteAsync($"{item.Key},{range.ToCidr()},{url},{organisation}", token)
                        .ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);

        output.WriteLine();
        output.WriteLine($"  {answered,8:N0} answered");
        output.WriteLine($"  {failed,8:N0} did not");
        output.WriteLine($"  {references,8:N0} geofeed references found");
        output.WriteLine();
        output.WriteLine($"wrote {outPath}");
        output.WriteLine();
        output.WriteLine("next:");
        output.WriteLine($"  eqatlas geofeeds --references {outPath} --out geofeeds.csv --same-org");
        return 0;
    }

    private readonly record struct Delegation(string Registry, string Key, string Url, AtlasEntry Range);

    /// <summary>The blocks a delegated-extended file says a registry handed out.</summary>
    private static IEnumerable<Delegation> Read(string path, TextWriter error)
    {
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(path);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length < 7 || fields[1] is "*" or "" || fields[6] is not ("allocated" or "assigned"))
            {
                continue;
            }

            var registry = fields[0];
            if (National.TryGetValue((registry, fields[1]), out var national))
            {
                registry = national.Name;
            }
            else if (!Endpoints.ContainsKey(registry))
            {
                unknown.Add(registry);
                continue;
            }

            var endpoint = national.Endpoint ?? Endpoints[registry];

            if (fields[2] is not ("ipv4" or "ipv6") || !IPAddress.TryParse(fields[3], out var start))
            {
                continue;
            }

            var range = RirDelegatedParser.Parse(new StringReader(line)).FirstOrDefault();
            if (range.Start > range.End)
            {
                continue;
            }

            yield return new Delegation(registry, $"{registry}/{fields[3]}", endpoint + start, range);
        }

        foreach (var registry in unknown)
        {
            error.WriteLine($"eqatlas: no RDAP endpoint known for registry '{registry}', skipped");
        }
    }

    /// <summary>What a previous run already asked about.</summary>
    private static HashSet<string> Resume(string path, TextWriter output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return seen;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var comma = line.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                seen.Add(line[..comma]);
            }
        }

        output.WriteLine($"  resuming: {seen.Count:N0} delegations already recorded in {Path.GetFileName(path)}");
        return seen;
    }

    /// <summary>
    /// Appends results to the resume file as they arrive, flushing often enough
    /// that a killed run loses seconds of work rather than an hour of it.
    /// </summary>
    private sealed class ResultSink(string path, bool fresh) : IAsyncDisposable
    {
        private const int FlushEvery = 200;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private StreamWriter? _writer;
        private int _sinceFlush;

        public async Task WriteAsync(string line, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_writer is null)
                {
                    _writer = AppendSafely.Open(path);
                    if (fresh)
                    {
                        await _writer.WriteLineAsync("# delegation,range,geofeed url,organisation").ConfigureAwait(false);
                        await _writer.WriteLineAsync(
                            "# Every delegation asked about is recorded, including those with no geofeed,").ConfigureAwait(false);
                        await _writer.WriteLineAsync(
                            "# so this file is both an audit of what was checked and a resume point.").ConfigureAwait(false);
                    }
                }

                await _writer.WriteLineAsync(line).ConfigureAwait(false);
                if (++_sinceFlush >= FlushEvery)
                {
                    await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    _sinceFlush = 0;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_writer is not null)
            {
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
            }

            _gate.Dispose();
        }
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
