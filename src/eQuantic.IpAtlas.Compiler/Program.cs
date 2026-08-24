using System.Globalization;
using eQuantic.IpAtlas.Compiler;

// A tool's output gets read by scripts and pasted into tickets. Coordinates and
// counts must not change shape with the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
eqatlas — compiles .eqatlas IP geolocation datasets

  fetch   --into <dir> [--attempts <n>]
          Downloads every public source a world dataset is built from.

  build   --out <dataset.eqatlas>
          [--rir <delegated-extended>...]      registry delegations (base layer)
          [--asn <ip2asn.tsv>...]              autonomous system numbers
          [--geofeed <feed.csv>...]            RFC 8805 feeds, outrank delegations
          [--cloud <ranges.json>...]           AWS / Google / Azure published ranges
          [--anycast <cidrs.txt>...]           plain CIDR lists, flagged anycast
          [--override <feed.csv>...]           local corrections, outrank everything
          [--asn-heuristics]                   guess hosting from AS names (off by default)
          [--source <text>] [--built-at <date>]

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
