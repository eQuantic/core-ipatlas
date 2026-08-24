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
/// <param name="Widened">Prefixes accepted because the registry records them against the same organisation.</param>
/// <param name="Unauthorized">Prefixes a feed had no standing to describe.</param>
/// <param name="WorstOffenders">
/// The feeds that claimed the most ground they had no registry object for,
/// worst first. Naming them is the point: a feed rejecting nothing and a feed
/// rejecting a million prefixes are very different facts, and an aggregate
/// hides which one you have.
/// </param>
public readonly record struct HarvestReport(
    int References, int Feeds, int Fetched, int Unreadable, int Failed, int Accepted, int Widened, int Unauthorized,
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
        var references = args.ExistingFiles("references");
        var outPath = args.One("out", required: true);
        var concurrency = Number(args, "concurrency", 16, 1, 64);
        var timeout = Number(args, "timeout", 15, 1, 120);
        var limit = Number(args, "limit", int.MaxValue, 1, int.MaxValue);
        var attempts = Number(args, "attempts", 3, 1, 10);
        var sameOrganisation = args.Has("same-org");
        var perHost = Number(args, "per-host", 2, 1, 16);
        var cache = args.One("cache");

        if (dumps.Count == 0 && references.Count == 0)
        {
            args.Fail("give at least one of --whois (a registry database dump) or --references (an eqatlas rdap file)");
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
        var referencesTotal = 0;
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
                authorization.AllowOrganisation(reference.Organisation);
                found++;
            }

            referencesTotal += found;
            output.WriteLine($"  {Path.GetFileName(dump),-32} {found,8:N0} geofeed references");
        }

        foreach (var file in references)
        {
            var found = 0;
            foreach (var reference in ReadReferences(file))
            {
                if (reference.Url.Length == 0)
                {
                    continue; // an audit row: this delegation carried no geofeed
                }

                if (!index.TryGetValue(reference.Url, out var authorization))
                {
                    index[reference.Url] = authorization = new GeofeedAuthorization();
                }

                authorization.Allow(reference.Range);
                authorization.AllowOrganisation(reference.Organisation);
                found++;
            }

            referencesTotal += found;
            output.WriteLine($"  {Path.GetFileName(file),-32} {found,8:N0} geofeed references");
        }

        if (index.Count == 0)
        {
            error.WriteLine("eqatlas: no geofeed references in those sources");
            return 1;
        }

        if (sameOrganisation)
        {
            WidenToOrganisations(index, dumps, references, output);
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

        using var fetcher = new PoliteFetcher(TimeSpan.FromSeconds(timeout), attempts, perHost, cache);

        var (accepted, report) = await HarvestAsync(
            feeds,
            fetcher.GetAsync,
            concurrency,
            cancellationToken).ConfigureAwait(false);

        report = report with { References = referencesTotal, Feeds = planned };
        if (fetcher.NotModified > 0)
        {
            output.WriteLine();
            output.WriteLine($"  {fetcher.NotModified:N0} feeds answered \"unchanged\" and were served from cache");
        }

        await WriteFeedAsync(outPath!, accepted, cancellationToken).ConfigureAwait(false);
        Report(output, report, outPath!);
        return accepted.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Adds, to each feed's authorisation, every range the registry records
    /// against an organisation that published it.
    /// <para>
    /// A second pass over the dumps rather than one: the organisations worth
    /// looking for are only known once the first pass has read every geofeed
    /// reference, and holding every inetnum in a registry to avoid re-reading
    /// would cost far more memory than re-reading costs seconds.
    /// </para>
    /// </summary>
    private static void WidenToOrganisations(
        Dictionary<string, GeofeedAuthorization> index, IReadOnlyList<string> dumps,
        IReadOnlyList<string> references, TextWriter output)
    {
        var byOrganisation = new Dictionary<string, List<GeofeedAuthorization>>(StringComparer.OrdinalIgnoreCase);
        foreach (var authorization in index.Values)
        {
            foreach (var organisation in authorization.Organisations)
            {
                if (!byOrganisation.TryGetValue(organisation, out var feeds))
                {
                    byOrganisation[organisation] = feeds = [];
                }

                feeds.Add(authorization);
            }
        }

        if (byOrganisation.Count == 0)
        {
            output.WriteLine();
            output.WriteLine("  --same-org: no referencing object carried an org: handle, nothing to widen");
            return;
        }

        var wanted = new HashSet<string>(byOrganisation.Keys, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var dump in dumps)
        {
            foreach (var (range, organisation) in WhoisGeofeedIndex.ParseOrganisationRanges(dump, wanted))
            {
                foreach (var authorization in byOrganisation[organisation])
                {
                    authorization.AllowSameOrganisation(range);
                    added++;
                }
            }
        }

        // An RDAP run records the organisation for every delegation it asked
        // about, geofeed or not, so the same widening works there without a
        // second pass over anything.
        foreach (var file in references)
        {
            foreach (var reference in ReadReferences(file))
            {
                if (reference.Organisation is { Length: > 0 } handle
                    && byOrganisation.TryGetValue(handle, out var feeds))
                {
                    foreach (var authorization in feeds)
                    {
                        authorization.AllowSameOrganisation(reference.Range);
                        added++;
                    }
                }
            }
        }

        output.WriteLine();
        output.WriteLine(
            $"  --same-org: {byOrganisation.Count:N0} publishing organisations, {added:N0} further registry objects");
    }

    /// <summary>Orders the harvest deterministically, whatever order it arrived in.</summary>
    private static int CompareForOutput(AtlasEntry left, AtlasEntry right)
    {
        var result = left.IsV6.CompareTo(right.IsV6);
        if (result != 0)
        {
            return result;
        }

        result = left.Start.CompareTo(right.Start);
        if (result != 0)
        {
            return result;
        }

        result = left.End.CompareTo(right.End);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(left.CountryCode, right.CountryCode);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(left.Region, right.Region);
        return result != 0 ? result : string.CompareOrdinal(left.City, right.City);
    }

    /// <summary>
    /// Reads what an <c>eqatlas rdap</c> run recorded. Rows with no URL are the
    /// delegations that carried no geofeed; they are kept in the file as an
    /// audit of what was checked, and they still carry an organisation, which
    /// is what <c>--same-org</c> widens from.
    /// </summary>
    private static IEnumerable<GeofeedReference> ReadReferences(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 3 || WhoisGeofeedIndex.ParseRange(fields[1]) is not { } range)
            {
                continue;
            }

            var organisation = fields.Length > 3 && fields[3].Length > 0 ? fields[3] : null;
            yield return new GeofeedReference(range, fields[2], organisation);

        }
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
        int fetched = 0, unreadable = 0, failed = 0, unauthorized = 0, widenedTotal = 0, feedCount = 0;

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
                var widened = 0;
                using (var reader = new StringReader(body))
                {
                    foreach (var entry in GeofeedParser.Parse(reader))
                    {
                        parsed++;
                        switch (feed.Value.Covers(entry))
                        {
                            case Coverage.Referenced:
                                kept.Add(entry);
                                break;
                            case Coverage.SameOrganisation:
                                kept.Add(entry);
                                widened++;
                                break;
                            default:
                                rejected++;
                                break;
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
                Interlocked.Add(ref widenedTotal, widened);
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

        // A total order, not just family and start. List.Sort is unstable, so
        // ties were being broken by whichever parallel fetch happened to finish
        // first — which made two harvests of the same feeds differ by a few
        // swapped lines. Reproducibility that only holds when the network
        // cooperates is not reproducibility.
        accepted.Sort(CompareForOutput);

        return (accepted, new HarvestReport(
            0, feedCount, fetched, unreadable, failed, accepted.Count, widenedTotal, unauthorized,
            offenders.OrderByDescending(entry => entry.Rejected).Take(5).ToList()));
    }

    private static void Report(TextWriter output, HarvestReport report, string outPath)
    {
        output.WriteLine();
        output.WriteLine($"  {report.Fetched,8:N0} feeds read");
        output.WriteLine($"  {report.Unreadable,8:N0} answered with something that is not a geofeed");
        output.WriteLine($"  {report.Failed,8:N0} could not be reached");
        output.WriteLine();
        output.WriteLine($"  {report.Accepted,8:N0} prefixes accepted"
            + (report.Widened > 0
                ? $", {report.Widened:N0} of them on the registry's word that the same organisation holds them"
                : string.Empty));
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

    private static string Shorten(string url) => url.Length <= 72 ? url : url[..69] + "...";

    /// <summary>Writes the harvest as an RFC 8805 file, atomically.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="entries">The accepted prefixes, already sorted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task WriteFeedAsync(
        string path, List<AtlasEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var temporary = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");

        var text = new StringBuilder();
        text.AppendLine("# Harvested from registry geofeed references (RFC 9092).");
        text.AppendLine("# Every prefix here was published by the holder of the registry object");
        text.AppendLine("# that pointed at its feed; anything a feed claimed beyond that was dropped.");
        foreach (var entry in entries)
        {
            // All five RFC 8805 fields, trailing empty postal code included: the
            // file this writes is a geofeed like any other and should read as one.
            text.Append(CultureInfo.InvariantCulture, $"{entry.ToCidr()},{entry.CountryCode}");
            text.Append(CultureInfo.InvariantCulture, $",{entry.Region},{entry.City},");
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
