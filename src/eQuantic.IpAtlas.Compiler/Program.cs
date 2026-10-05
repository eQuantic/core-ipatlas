using System.Globalization;
using eQuantic.IpAtlas.Compiler;

// A tool's output gets read by scripts and pasted into tickets. Coordinates and
// counts must not change shape with the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
eqatlas — compiles .eqatlas IP geolocation datasets

  fetch   --into <dir> [--attempts <n>] [--with-whois]
          Downloads every public source a world dataset is built from.
          --with-whois also pulls the registry database dumps that
          `eqatlas geofeeds` reads (a few hundred megabytes).

  build   --out <dataset.eqatlas>
          [--rir <delegated-extended>...]      registry delegations (base layer)
          [--asn <ip2asn.tsv>...]              autonomous system numbers
          [--geofeed <feed.csv>...]            RFC 8805 feeds, outrank delegations
          [--cloud <ranges.json>...]           AWS / Google / Azure published ranges
          [--anycast <cidrs.txt>...]           plain CIDR lists, flagged anycast
          [--anonymizer <ips.txt>...]          VPN, proxy and Tor exit lists
          [--override <feed.csv>...]           local corrections, outrank everything
          [--asn-heuristics]                   guess hosting from AS names (off by default)
          [--source <text>] [--built-at <date>]

  rdap    --delegated <delegated-extended>... --out <references.csv>
          [--concurrency <n>] [--per-host <n>] [--timeout <s>] [--attempts <n>] [--limit <n>]
          Asks a registry about every block it delegated, to find the geofeeds
          of operators whose registry publishes no bulk database. Resumable:
          re-running skips what an earlier run already recorded.

  geofeeds [--whois <registry.db.gz>...] [--references <rdap.csv>...] --out <geofeeds.csv>
          [--concurrency <n>] [--timeout <seconds>] [--attempts <n>] [--limit <n>]
          [--per-host <n>] [--cache <dir>] [--same-org]
          Harvests the geofeeds operators publish about their own networks,
          keeping only what each one is authorised to claim (RFC 9092).
          --same-org also accepts prefixes the registry records against an
          organisation that published the feed. Still the registry's word,
          not the feed's, and reported separately.

  accuracy --dataset <dataset.eqatlas> --truth <cloud-ranges.json>...
          [--baseline <other.eqatlas>] [--min-correct <percent>]
          Scores a dataset against providers' own published regions.

  verify  --dataset <dataset.eqatlas> [--max-age-days <n>]
          Checks a dataset is intact and says how old it is.

  lookup  --dataset <dataset.eqatlas> --ip <address>...
          Answers for one or more addresses.
""";

var parsed = Arguments.Parse(args);
switch (parsed.Command)
{
    case "fetch":
        return await FetchCommand.RunAsync(parsed, Console.Out, Console.Error, CancellationToken.None)
            .ConfigureAwait(false);
    case "build":
        return BuildCommand.Run(parsed, Console.Out, Console.Error);
    case "rdap":
        return await RdapCommand.RunAsync(parsed, Console.Out, Console.Error, CancellationToken.None)
            .ConfigureAwait(false);
    case "geofeeds":
        return await GeofeedsCommand.RunAsync(parsed, Console.Out, Console.Error, CancellationToken.None)
            .ConfigureAwait(false);
    case "accuracy":
        return AccuracyCommand.Run(parsed, Console.Out, Console.Error);
    case "verify":
        return InspectCommands.Verify(parsed, Console.Out, Console.Error);
    case "lookup":
        return InspectCommands.Lookup(parsed, Console.Out, Console.Error);
    case null:
        Console.Error.WriteLine(Usage);
        return 2;
    default:
        Console.Error.WriteLine($"eqatlas: unknown command '{parsed.Command}'");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 2;
}
