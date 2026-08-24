namespace eQuantic.IpAtlas.Compiler;

/// <summary>On what grounds a feed was allowed to describe a range.</summary>
public enum Coverage : byte
{
    /// <summary>No grounds. The claim is discarded.</summary>
    None = 0,

    /// <summary>A registry object naming this feed covers the range. This is RFC 9092.</summary>
    Referenced = 1,

    /// <summary>
    /// The registry records the range against the same organisation that
    /// published the feed, though that particular object does not name it.
    /// </summary>
    SameOrganisation = 2,
}

/// <summary>
/// What one geofeed URL is allowed to make claims about.
/// <para>
/// A geofeed is a CSV on a web server, and the web server has no idea which
/// addresses its owner holds. Without this check, importing geofeeds would mean
/// that anyone who can publish a file and get one line into a registry object
/// can relocate any prefix on the internet — including someone else's. RFC 9092
/// draws the boundary where it belongs: a feed speaks only for the registry
/// objects that pointed at it.
/// </para>
/// <para>
/// That boundary is strict enough to discard a great deal that is almost
/// certainly true, because operators annotate a few objects and publish a feed
/// for everything. <see cref="AllowOrganisation"/> widens it by one step that
/// keeps the registry as the authority rather than the feed: a range the
/// registry records against the same <c>org:</c> handle that published the feed
/// is corroborated by the registry, not by the publisher's say-so. A feed lying
/// about somebody else's addresses still fails, because the handles will not
/// match. Which grounds each claim was accepted on is reported, never merged.
/// </para>
/// </summary>
public sealed class GeofeedAuthorization
{
    private readonly List<(UInt128 Start, UInt128 End)> _referencedV4 = [];
    private readonly List<(UInt128 Start, UInt128 End)> _referencedV6 = [];
    private readonly List<(UInt128 Start, UInt128 End)> _organisationV4 = [];
    private readonly List<(UInt128 Start, UInt128 End)> _organisationV6 = [];
    private readonly HashSet<string> _organisations = new(StringComparer.OrdinalIgnoreCase);
    private bool _sealed;

    /// <summary>How many disjoint ranges a registry object named this feed for.</summary>
    public int ReferencedRangeCount => _referencedV4.Count + _referencedV6.Count;

    /// <summary>The organisations that published this feed, per the objects that named it.</summary>
    public IReadOnlyCollection<string> Organisations => _organisations;

    /// <summary>Adds a registry object that pointed at the feed.</summary>
    public void Allow(AtlasEntry range)
    {
        RequireOpen();
        (range.IsV6 ? _referencedV6 : _referencedV4).Add((range.Start, range.End));
    }

    /// <summary>Records the organisation a referencing object belonged to.</summary>
    public void AllowOrganisation(string? organisation)
    {
        RequireOpen();
        if (!string.IsNullOrWhiteSpace(organisation))
        {
            _organisations.Add(organisation);
        }
    }

    /// <summary>Adds a range the registry records against one of this feed's organisations.</summary>
    public void AllowSameOrganisation(AtlasEntry range)
    {
        RequireOpen();
        (range.IsV6 ? _organisationV6 : _organisationV4).Add((range.Start, range.End));
    }

    /// <summary>Merges the allowed ranges so containment is a binary search.</summary>
    public GeofeedAuthorization Compact()
    {
        Merge(_referencedV4);
        Merge(_referencedV6);
        Merge(_organisationV4);
        Merge(_organisationV6);
        _sealed = true;
        return this;
    }

    /// <summary>On what grounds, if any, the feed may say something about this range.</summary>
    /// <exception cref="InvalidOperationException">
    /// The allowed ranges were never compacted, so they are neither sorted nor
    /// disjoint and a search over them would answer nonsense. Failing loudly
    /// beats a check that silently stops checking.
    /// </exception>
    public Coverage Covers(AtlasEntry entry)
    {
        if (!_sealed)
        {
            throw new InvalidOperationException("Call Compact() before testing coverage.");
        }

        if (Contains(entry.IsV6 ? _referencedV6 : _referencedV4, entry))
        {
            return Coverage.Referenced;
        }

        return Contains(entry.IsV6 ? _organisationV6 : _organisationV4, entry)
            ? Coverage.SameOrganisation
            : Coverage.None;
    }

    private void RequireOpen()
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The authorization has already been compacted.");
        }
    }

    private static bool Contains(List<(UInt128 Start, UInt128 End)> ranges, AtlasEntry entry)
    {
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
