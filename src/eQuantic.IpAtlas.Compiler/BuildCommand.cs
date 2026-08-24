using System.Globalization;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>Builds a dataset from every source the caller supplied.</summary>
public static class BuildCommand
{
    /// <summary>Runs the build, returning a process exit code.</summary>
    public static int Run(Arguments args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var rir = args.ExistingFiles("rir");
        var asn = args.ExistingFiles("asn");
        var geofeed = args.ExistingFiles("geofeed");
        var cloud = args.ExistingFiles("cloud");
        var anycast = args.ExistingFiles("anycast");
        var overrides = args.ExistingFiles("override");
        var outPath = args.One("out", required: true);
        var source = args.One("source");
        var builtAtText = args.One("built-at");

        if (rir.Count == 0 && geofeed.Count == 0 && cloud.Count == 0)
        {
            args.Fail("give at least one of --rir, --geofeed or --cloud");
        }

        var builtAt = DateTimeOffset.UtcNow;
        if (builtAtText is not null
            && !DateTimeOffset.TryParse(builtAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out builtAt))
        {
            args.Fail($"--built-at '{builtAtText}' is not a date");
        }

        if (args.Errors.Count > 0)
        {
            foreach (var message in args.Errors)
            {
                error.WriteLine($"eqatlas: {message}");
            }

            return 2;
        }

        var builder = new DatasetBuilder();
        var rejected = 0;

        foreach (var file in rir)
        {
            using var reader = new StreamReader(file);
            var entries = RirDelegatedParser.Parse(reader, out var counters).ToList();
            builder.AddRegistry(entries);
            rejected += counters.Malformed + counters.OutOfRange;
            output.WriteLine($"  registry  {Path.GetFileName(file),-42} {entries.Count,9:N0} ranges"
                + (counters.AnyRejected
                    ? $"  ({counters.Malformed} malformed, {counters.OutOfRange} out of range)"
                    : string.Empty));
        }

        var heuristics = args.Has("asn-heuristics");
        foreach (var file in asn)
        {
            using var reader = new StreamReader(file);
            var entries = AsnTsvParser.Parse(reader, heuristics).ToList();
            builder.AddAsns(entries);
            output.WriteLine($"  asn       {Path.GetFileName(file),-42} {entries.Count,9:N0} ranges"
                + (heuristics ? "  (name heuristics on)" : string.Empty));
        }

        foreach (var file in geofeed)
        {
            using var reader = new StreamReader(file);
            var entries = GeofeedParser.Parse(reader).ToList();
            builder.AddGeofeed(entries);
            output.WriteLine($"  geofeed   {Path.GetFileName(file),-42} {entries.Count,9:N0} ranges");
        }

        foreach (var file in cloud)
        {
            using var stream = File.OpenRead(file);
            List<AtlasEntry> entries;
            try
            {
                entries = CloudRangesParser.Parse(stream).ToList();
            }
            catch (System.Text.Json.JsonException ex)
            {
                error.WriteLine($"eqatlas: --cloud '{file}' is not readable JSON: {ex.Message}");
                return 2;
            }

            builder.AddCloud(entries);
            var located = entries.Count(entry => entry.CountryCode is not null);
            output.WriteLine(
                $"  cloud     {Path.GetFileName(file),-42} {entries.Count,9:N0} ranges  ({located:N0} located)");
        }

        foreach (var file in anycast)
        {
            using var reader = new StreamReader(file);
            var entries = CloudRangesParser.ParseCidrList(reader, IpFlags.Hosting | IpFlags.Anycast).ToList();
            builder.AddCloud(entries);
            output.WriteLine($"  anycast   {Path.GetFileName(file),-42} {entries.Count,9:N0} ranges");
        }

        foreach (var file in overrides)
        {
            using var reader = new StreamReader(file);
            var entries = GeofeedParser.Parse(reader).ToList();
            builder.AddOverrides(entries);
            output.WriteLine($"  override  {Path.GetFileName(file),-42} {entries.Count,9:N0} ranges");
        }

        // Write beside the target and rename into place. A dataset is usually
        // rebuilt over the one a service is already serving, and a build that
        // dies halfway must not be able to leave a truncated file where a good
        // one used to be.
        var directory = Path.GetDirectoryName(Path.GetFullPath(outPath!));
        var temporary = Path.Combine(
            directory ?? ".", $".{Path.GetFileName(outPath)}.{Environment.ProcessId}.tmp");

        BuildReport report;
        try
        {
            using (var stream = File.Create(temporary))
            {
                report = builder.Write(stream, source ?? Describe(rir, asn, geofeed, cloud), builtAt);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, outPath!, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"eqatlas: could not write '{outPath}': {ex.Message}");
            TryDelete(temporary);
            return 1;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        output.WriteLine();
        output.WriteLine($"  {report.V4Ranges,9:N0} IPv4 ranges");
        output.WriteLine($"  {report.V6Ranges,9:N0} IPv6 ranges");
        output.WriteLine($"  {report.Locations,9:N0} distinct places");
        output.WriteLine();
        output.WriteLine("  country by source:");
        output.WriteLine($"  {report.CountryFromCloud,9:N0} cloud provider");
        output.WriteLine($"  {report.CountryFromGeofeed,9:N0} operator geofeed");
        output.WriteLine($"  {report.CountryFromRegistry,9:N0} registry delegation");
        if (rejected > 0)
        {
            output.WriteLine();
            output.WriteLine($"  {rejected:N0} source records rejected");
        }

        output.WriteLine();
        output.WriteLine($"wrote {outPath} ({report.Bytes:N0} bytes)");
        return 0;
    }

    private static string Describe(
        IReadOnlyList<string> rir, IReadOnlyList<string> asn,
        IReadOnlyList<string> geofeed, IReadOnlyList<string> cloud)
    {
        var parts = new List<string>();
        if (rir.Count > 0)
        {
            parts.Add($"rir:{rir.Count}");
        }

        if (asn.Count > 0)
        {
            parts.Add($"asn:{asn.Count}");
        }

        if (geofeed.Count > 0)
        {
            parts.Add($"geofeed:{geofeed.Count}");
        }

        if (cloud.Count > 0)
        {
            parts.Add($"cloud:{cloud.Count}");
        }

        return string.Join(' ', parts);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The build already failed; a leftover temp file is not worth a second error.
        }
    }
}
