namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// What one geofeed URL is allowed to make claims about.
/// <para>
/// A geofeed is a CSV on a web server, and the web server has no idea which
/// addresses its owner holds. Without this check, importing geofeeds would mean
/// that anyone who can publish a file and get one line into a registry object
/// can relocate any prefix on the internet — including someone else's. RFC 9092
/// draws the boundary where it belongs: a feed speaks only for the registry
/// objects that pointed at it. Everything outside those ranges is discarded and
/// counted, never quietly trimmed.
/// </para>
/// </summary>
public sealed class GeofeedAuthorization
{
    private readonly List<(UInt128 Start, UInt128 End)> _v4 = [];
    private readonly List<(UInt128 Start, UInt128 End)> _v6 = [];
    private bool _sealed;

    /// <summary>How many disjoint ranges this feed is allowed to speak for.</summary>
    public int RangeCount => _v4.Count + _v6.Count;

    /// <summary>Adds a registry object that pointed at the feed.</summary>
    public void Allow(AtlasEntry range)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The authorization has already been compacted.");
        }

        (range.IsV6 ? _v6 : _v4).Add((range.Start, range.End));
    }

    /// <summary>Merges the allowed ranges so containment is a binary search.</summary>
    public GeofeedAuthorization Compact()
    {
        Merge(_v4);
        Merge(_v6);
        _sealed = true;
        return this;
    }

    /// <summary>Whether the feed may say anything about this range.</summary>
    /// <exception cref="InvalidOperationException">
    /// The allowed ranges were never compacted, so they are neither sorted nor
    /// disjoint and a search over them would answer nonsense. Failing loudly
    /// beats a check that silently stops checking.
    /// </exception>
    public bool Covers(AtlasEntry entry)
    {
        if (!_sealed)
        {
            throw new InvalidOperationException("Call Compact() before testing coverage.");
        }

        var ranges = entry.IsV6 ? _v6 : _v4;
        var low = 0;
        var high = ranges.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (ranges[middle].Start > entry.Start)
            {
                high = middle - 1;
            }
            else if (ranges[middle].End < entry.Start)
            {
                low = middle + 1;
            }
            else
            {
                return entry.End <= ranges[middle].End;
            }
        }

        return false;
    }

    private static void Merge(List<(UInt128 Start, UInt128 End)> ranges)
    {
        if (ranges.Count == 0)
        {
            return;
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var write = 0;
        for (var read = 1; read < ranges.Count; read++)
        {
            var current = ranges[read];
            var previous = ranges[write];
            if (current.Start <= previous.End
                || (previous.End != UInt128.MaxValue && current.Start == previous.End + UInt128.One))
            {
                if (current.End > previous.End)
                {
                    ranges[write] = (previous.Start, current.End);
                }
            }
            else
            {
                ranges[++write] = current;
            }
        }

        ranges.RemoveRange(write + 1, ranges.Count - write - 1);
    }
}
