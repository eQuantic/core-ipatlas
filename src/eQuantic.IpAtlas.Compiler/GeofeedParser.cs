namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Parses RFC 8805 geofeeds: <c>prefix,country,region,city,postal</c>, one CIDR
/// per line.
/// <para>
/// This is the file a network operator publishes to say where its own addresses
/// are, and it is the only free source that beats a registry delegation. A
/// registry records who was handed a block and in which country the paperwork
/// sat; the operator records where the traffic actually comes out. When the two
/// disagree, the operator is right, which is why geofeed entries outrank
/// delegations in the builder.
/// </para>
/// Signature blocks from RFC 9632 and comment lines are skipped.
/// </summary>
public static class GeofeedParser
{
    /// <summary>Yields the located prefixes the feed records.</summary>
    public static IEnumerable<AtlasEntry> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (reader.ReadLine() is { } line)
        {
            var text = line.AsSpan().Trim();
            if (text.IsEmpty || text[0] is '#' or '-')
            {
                continue;
            }

            var fields = line.Split(',');
            var country = Field(fields, 1);
            var region = Field(fields, 2);
            var city = Field(fields, 3);

            // A geofeed line with no place at all is a deliberate "I am not
            // saying" from the operator, not data — recording it as a located
            // range would overwrite a delegation with nothing.
            if (country is null && region is null && city is null)
            {
                continue;
            }

            if (AtlasEntry.FromPrefix(fields[0].Trim(), country, region: region, city: city) is { } entry)
            {
                yield return entry;
            }
        }
    }

    private static string? Field(string[] fields, int index) =>
        index < fields.Length && fields[index].Trim() is { Length: > 0 } value ? value : null;
}
