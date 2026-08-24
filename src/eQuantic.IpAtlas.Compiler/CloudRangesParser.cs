using System.Text.Json;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Reads the address ranges the big cloud providers publish about themselves,
/// and turns each one into a located, hosting-flagged range.
/// <para>
/// AWS, Google and Microsoft each ship a machine-readable file naming the
/// region every prefix belongs to. Joined with <see cref="CloudRegions"/> that
/// is authoritative geolocation straight from the operator, and it corrects the
/// single largest error in registry-derived data: cloud space registered in one
/// country and run in thirty others. Ranges whose region is global or unknown
/// still earn the hosting flag, and global ones the anycast flag, because
/// "this is a datacenter" is itself the signal a risk check needs.
/// </para>
/// </summary>
public static class CloudRangesParser
{
    /// <summary>Reads AWS, Google Cloud or Azure published ranges, detecting which from the shape.</summary>
    public static IEnumerable<AtlasEntry> Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("values", out var values))
        {
            return ReadAzure(values).ToList();
        }

        if (root.TryGetProperty("prefixes", out var prefixes))
        {
            var entries = ReadAws(prefixes).Concat(ReadGoogle(prefixes)).ToList();
            if (root.TryGetProperty("ipv6_prefixes", out var v6))
            {
                entries.AddRange(ReadAws(v6));
            }

            return entries;
        }

        return [];
    }

    private static IEnumerable<AtlasEntry> ReadAws(JsonElement prefixes)
    {
        foreach (var element in prefixes.EnumerateArray())
        {
            var prefix = Text(element, "ip_prefix") ?? Text(element, "ipv6_prefix");
            if (prefix is null)
            {
                continue;
            }

            if (Build(prefix, Text(element, "region")) is { } entry)
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<AtlasEntry> ReadGoogle(JsonElement prefixes)
    {
        foreach (var element in prefixes.EnumerateArray())
        {
            var prefix = Text(element, "ipv4Prefix") ?? Text(element, "ipv6Prefix");
            if (prefix is null)
            {
                continue;
            }

            if (Build(prefix, Text(element, "scope")) is { } entry)
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<AtlasEntry> ReadAzure(JsonElement values)
    {
        foreach (var element in values.EnumerateArray())
        {
            if (!element.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty("addressPrefixes", out var prefixes))
            {
                continue;
            }

            var region = Text(properties, "region");
            foreach (var prefix in prefixes.EnumerateArray())
            {
                if (prefix.GetString() is { } text && Build(text, region) is { } entry)
                {
                    yield return entry;
                }
            }
        }
    }

    private static AtlasEntry? Build(string prefix, string? region)
    {
        var isGlobal = string.IsNullOrEmpty(region)
            || region.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)
            || region.Equals("global", StringComparison.Ordinal);

        var flags = isGlobal ? NetworkTraits.Hosting | NetworkTraits.Anycast : NetworkTraits.Hosting;
        if (CloudRegions.Get(region) is not { } place)
        {
            return AtlasEntry.FromPrefix(prefix, traits: flags);
        }

        return AtlasEntry.FromPrefix(
            prefix, place.CountryCode, traits: flags,
            latitude: place.Latitude, longitude: place.Longitude, region: region, city: place.City);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
