using System.Globalization;
using System.Text;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Records each feed's accepted prefixes the moment that feed is done, so a
/// harvest killed near the end resumes instead of starting over.
/// <para>
/// The obvious fix — stream the output as it arrives — does not work here,
/// because the harvest must be sorted to be reproducible and a stream cannot
/// be sorted before it ends. So the durable thing and the finished thing are
/// separate: this spool is append-only and unordered, and the harvest is
/// assembled and sorted from it at the end. The spool is deleted once the real
/// output exists.
/// </para>
/// <para>
/// A feed's rows are followed by a marker naming it. Rows without one are what
/// a kill mid-feed left behind, and they are ignored — a half-written feed must
/// not be mistaken for a finished one.
/// </para>
/// </summary>
public sealed class HarvestSpool : IAsyncDisposable
{
    private const string DoneMarker = "#done,";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private StreamWriter? _writer;

    /// <summary>Opens the spool that belongs to an output file.</summary>
    public HarvestSpool(string outputPath)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        _path = outputPath + ".partial";
    }

    /// <summary>Where the spool lives, for reporting.</summary>
    public string Path => _path;

    /// <summary>Feeds a previous run finished, with what each contributed.</summary>
    public static (HashSet<string> Done, List<AtlasEntry> Entries) Recover(string outputPath)
    {
        ArgumentNullException.ThrowIfNull(outputPath);

        var done = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Dictionary<string, List<AtlasEntry>>(StringComparer.Ordinal);
        var path = outputPath + ".partial";
        if (!File.Exists(path))
        {
            return (done, []);
        }

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith(DoneMarker, StringComparison.Ordinal))
            {
                done.Add(line[DoneMarker.Length..]);
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 5)
            {
                continue;
            }

            if (AtlasEntry.FromPrefix(fields[1], Blank(fields[2]), region: Blank(fields[3]), city: Blank(fields[4]))
                is { } entry)
            {
                if (!pending.TryGetValue(fields[0], out var list))
                {
                    pending[fields[0]] = list = [];
                }

                list.Add(entry);
            }
        }

        // Only the feeds that got as far as their marker are trusted.
        var entries = new List<AtlasEntry>();
        foreach (var (url, list) in pending)
        {
            if (done.Contains(url))
            {
                entries.AddRange(list);
            }
        }

        return (done, entries);
    }

    /// <summary>Records everything one feed contributed, then marks it finished.</summary>
    public async Task CompleteAsync(string url, IReadOnlyList<AtlasEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(entries);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _writer ??= new StreamWriter(_path, append: true);

            var text = new StringBuilder();
            foreach (var entry in entries)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"{url}\t{entry.ToCidr()}\t{entry.CountryCode}\t{entry.Region}\t{entry.City}\n");
            }

            text.Append(DoneMarker).Append(url).Append('\n');
            await _writer.WriteAsync(text, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the spool, once the finished output has been written.</summary>
    public void Discard()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Leaving it behind costs a stale resume point, not correctness:
            // the next run's feed list decides what is actually reused.
        }
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    /// <summary>Closes the spool.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
