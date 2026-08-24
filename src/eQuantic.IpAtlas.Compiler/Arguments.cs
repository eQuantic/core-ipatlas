namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Command-line arguments, parsed without throwing at the user. Every failure
/// here is someone mistyping a flag, and the answer to that is a sentence
/// saying which flag, not a stack trace.
/// </summary>
public sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];

    /// <summary>The command word, or null when none was given.</summary>
    public string? Command { get; private init; }

    /// <summary>What could not be parsed.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Parses <c>command --flag value value --flag value</c>.</summary>
    public static Arguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parsed = new Arguments { Command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null };
        for (var i = parsed.Command is null ? 0 : 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                parsed._errors.Add($"unexpected argument '{args[i]}'");
                continue;
            }

            var name = args[i][2..];
            if (name.Length == 0)
            {
                parsed._errors.Add("empty flag '--'");
                continue;
            }

            var values = parsed._values.TryGetValue(name, out var existing) ? existing : parsed._values[name] = [];
            while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values.Add(args[++i]);
            }
        }

        return parsed;
    }

    /// <summary>Whether the flag was given at all.</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>Every value given for a flag.</summary>
    public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var values) ? values : [];

    /// <summary>The single value for a flag, recording an error when it is missing or repeated.</summary>
    public string? One(string name, bool required = false)
    {
        if (!_values.TryGetValue(name, out var values) || values.Count == 0)
        {
            if (required)
            {
                _errors.Add($"--{name} is required");
            }

            return null;
        }

        if (values.Count > 1)
        {
            _errors.Add($"--{name} takes one value, got {values.Count}");
        }

        return values[0];
    }

    /// <summary>Records a problem found after parsing.</summary>
    public void Fail(string message) => _errors.Add(message);

    /// <summary>Files that must exist, reporting the ones that do not.</summary>
    public IReadOnlyList<string> ExistingFiles(string name)
    {
        var found = new List<string>();
        foreach (var path in All(name))
        {
            if (File.Exists(path))
            {
                found.Add(path);
            }
            else
            {
                _errors.Add($"--{name}: no such file '{path}'");
            }
        }

        return found;
    }
}
