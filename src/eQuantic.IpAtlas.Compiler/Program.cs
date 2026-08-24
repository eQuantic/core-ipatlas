using eQuantic.IpAtlas.Compiler;

// eqatlas build --rir <file>... [--asn <tsv>...] --out <path> [--source <text>]
if (args.Length == 0 || args[0] != "build")
{
    Console.Error.WriteLine("usage: eqatlas build --rir <delegated-extended-file>... [--asn <ip2asn-tsv>...] --out <dataset.eqatlas> [--source <text>]");
    return 1;
}

var rirFiles = new List<string>();
var asnFiles = new List<string>();
string? outPath = null;
string? source = null;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rir":
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                rirFiles.Add(args[++i]);
            }

            break;
        case "--asn":
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                asnFiles.Add(args[++i]);
            }

            break;
        case "--out":
            outPath = args[++i];
            break;
        case "--source":
            source = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 1;
    }
}

if (rirFiles.Count == 0 || outPath is null)
{
    Console.Error.WriteLine("at least one --rir file and --out are required");
    return 1;
}

var builder = new DatasetBuilder();
foreach (var file in rirFiles)
{
    using var reader = new StreamReader(file);
    builder.AddCountries(RirDelegatedParser.Parse(reader));
    Console.WriteLine($"rir: {Path.GetFileName(file)}");
}

foreach (var file in asnFiles)
{
    using var reader = new StreamReader(file);
    builder.AddAsns(AsnTsvParser.Parse(reader));
    Console.WriteLine($"asn: {Path.GetFileName(file)}");
}

using var output = File.Create(outPath);
builder.Write(
    output,
    source ?? $"rir:{rirFiles.Count} asn:{asnFiles.Count}",
    DateTimeOffset.UtcNow);
Console.WriteLine($"wrote {outPath} ({output.Length:N0} bytes)");
return 0;
