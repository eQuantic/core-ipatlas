using System.Globalization;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>How a dataset scored against one body of ground truth.</summary>
/// <param name="Truth">What it was measured against.</param>
/// <param name="Samples">How many addresses were checked.</param>
/// <param name="Correct">How many matched.</param>
/// <param name="Wrong">How many named a different country.</param>
/// <param name="Unknown">How many the dataset had no country for.</param>
public readonly record struct AccuracyScore(string Truth, int Samples, int Correct, int Wrong, int Unknown)
{
    /// <summary>Share of samples answered correctly.</summary>
    public double CorrectShare => Samples == 0 ? 0 : (double)Correct / Samples;

    /// <summary>Share of samples answered with the wrong country.</summary>
    public double WrongShare => Samples == 0 ? 0 : (double)Wrong / Samples;

    /// <summary>Share of samples with no answer at all.</summary>
    public double UnknownShare => Samples == 0 ? 0 : (double)Unknown / Samples;
}

/// <summary>
/// Measures a dataset against ground truth, because a geolocation library that
/// cannot show its accuracy is asking to be believed rather than trusted.
/// <para>
/// The truth used here is the region files the cloud providers publish about
/// their own networks: they are the only large body of free, authoritative
/// "this prefix is in this city" data there is. That cuts both ways, and the
/// command says so — if a dataset was built from the same file it is being
/// measured against, the number is a consistency check, not an independent
/// score. Pass <c>--baseline</c> to compare a dataset that was not built from
/// the truth file against one that was, which is the comparison that actually
/// says something.
/// </para>
/// </summary>
public static class AccuracyCommand
{
    /// <summary>Scores one or more datasets against published cloud ranges.</summary>
    public static int Run(Arguments args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var datasetPath = args.One("dataset", required: true);
        var baselinePath = args.One("baseline");
        var truthFiles = args.ExistingFiles("truth");
        var minimum = 0.0;
        if (args.One("min-correct") is { } text
            && (!double.TryParse(text, CultureInfo.InvariantCulture, out minimum) || minimum is < 0 or > 100))
        {
            args.Fail($"--min-correct '{text}' is not a percentage");
        }

        if (truthFiles.Count == 0)
        {
            args.Fail("--truth needs at least one published cloud range file");
        }

        if (args.Errors.Count > 0)
        {
            foreach (var message in args.Errors)
            {
                error.WriteLine($"eqatlas: {message}");
            }

            return 2;
        }

        if (!IpAtlasDatabase.TryOpen(datasetPath!, out var dataset, out var failure))
        {
            error.WriteLine($"eqatlas: {failure}");
            return 1;
        }

        IpAtlasDatabase? baseline = null;
        if (baselinePath is not null && !IpAtlasDatabase.TryOpen(baselinePath, out baseline, out var baselineFailure))
        {
            error.WriteLine($"eqatlas: {baselineFailure}");
            return 1;
        }

        var scores = new List<(AccuracyScore Measured, AccuracyScore? Baseline)>();
        foreach (var file in truthFiles)
        {
            var samples = Samples(file, error);
            if (samples.Count == 0)
            {
                error.WriteLine($"eqatlas: '{file}' carried no locatable prefixes");
                continue;
            }

            scores.Add((
                Score(Path.GetFileName(file), dataset!, samples),
                baseline is null ? null : Score(Path.GetFileName(file), baseline, samples)));
        }

        if (scores.Count == 0)
        {
            error.WriteLine("eqatlas: nothing to measure");
            return 1;
        }

        Report(output, scores, baselinePath is not null);

        var overall = Total(scores.Select(pair => pair.Measured));
        if (minimum > 0 && overall.CorrectShare * 100 < minimum)
        {
            error.WriteLine(
                $"eqatlas: {overall.CorrectShare * 100:F1}% correct is below the {minimum:F1}% required");
            return 1;
        }

        return 0;
    }

    private static void Report(
        TextWriter output, List<(AccuracyScore Measured, AccuracyScore? Baseline)> scores, bool hasBaseline)
    {
        output.WriteLine(hasBaseline
            ? "| truth | samples | correct | wrong | unknown | baseline correct |"
            : "| truth | samples | correct | wrong | unknown |");
        output.WriteLine(hasBaseline
            ? "|---|---:|---:|---:|---:|---:|"
            : "|---|---:|---:|---:|---:|");

        foreach (var (measured, baseline) in scores)
        {
            output.WriteLine(Row(measured, baseline, hasBaseline));
        }

        var overall = Total(scores.Select(pair => pair.Measured));
        var overallBaseline = hasBaseline
            ? Total(scores.Where(pair => pair.Baseline is not null).Select(pair => pair.Baseline!.Value))
            : (AccuracyScore?)null;
        output.WriteLine(Row(overall with { Truth = "**all**" }, overallBaseline, hasBaseline));
        output.WriteLine();
        output.WriteLine(
            "Truth is each provider's own published region file. A dataset built with");
        output.WriteLine(
            "--cloud from the same file is being checked for consistency, not accuracy;");
        output.WriteLine(
            "the baseline column is the comparison that measures something.");
    }

    private static string Row(AccuracyScore score, AccuracyScore? baseline, bool hasBaseline)
    {
        var row = string.Create(
            CultureInfo.InvariantCulture,
            $"| {score.Truth} | {score.Samples:N0} | {score.CorrectShare:P1} | {score.WrongShare:P1} | {score.UnknownShare:P1} |");

        return hasBaseline
            ? row + string.Create(
                CultureInfo.InvariantCulture,
                $" {(baseline is { } value ? value.CorrectShare.ToString("P1", CultureInfo.InvariantCulture) : "-")} |")
            : row;
    }

    private static AccuracyScore Total(IEnumerable<AccuracyScore> scores)
    {
        var total = new AccuracyScore("all", 0, 0, 0, 0);
        foreach (var score in scores)
        {
            total = total with
            {
                Samples = total.Samples + score.Samples,
                Correct = total.Correct + score.Correct,
                Wrong = total.Wrong + score.Wrong,
                Unknown = total.Unknown + score.Unknown,
            };
        }

        return total;
    }

    private static AccuracyScore Score(string truth, IpAtlasDatabase database, List<(UInt128 Address, bool IsV6, string Country)> samples)
    {
        int correct = 0, wrong = 0, unknown = 0;
        foreach (var (address, isV6, country) in samples)
        {
            var answer = database.Lookup(ToAddress(address, isV6)).CountryCode;
            if (answer is null)
            {
                unknown++;
            }
            else if (string.Equals(answer, country, StringComparison.OrdinalIgnoreCase))
            {
                correct++;
            }
            else
            {
                wrong++;
            }
        }

        return new AccuracyScore(truth, samples.Count, correct, wrong, unknown);
    }

    private static System.Net.IPAddress ToAddress(UInt128 value, bool isV6)
    {
        if (!isV6)
        {
            Span<byte> four = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(four, (uint)value);
            return new System.Net.IPAddress(four);
        }

        Span<byte> sixteen = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(sixteen, value);
        return new System.Net.IPAddress(sixteen);
    }

    /// <summary>One address from the middle of every located prefix in a truth file.</summary>
    private static List<(UInt128 Address, bool IsV6, string Country)> Samples(string file, TextWriter error)
    {
        var samples = new List<(UInt128, bool, string)>();
        try
        {
            using var stream = File.OpenRead(file);
            foreach (var entry in CloudRangesParser.Parse(stream))
            {
                if (entry.CountryCode is not { } country || entry.End <= entry.Start)
                {
                    continue;
                }

                samples.Add((entry.Start + ((entry.End - entry.Start) / 2), entry.IsV6, country));
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            error.WriteLine($"eqatlas: '{file}' is not readable JSON: {ex.Message}");
        }

        return samples;
    }
}
