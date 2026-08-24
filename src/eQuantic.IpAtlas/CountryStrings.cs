namespace eQuantic.IpAtlas;

/// <summary>
/// The 676 possible two-letter country codes, interned once. A lookup that hit
/// a range used to mint a fresh two-character string every time — 32 bytes of
/// garbage per answer, which at any real request rate is a measurable amount of
/// collection work for a value that has 676 possibilities. Now every answer
/// hands back the same instance.
/// </summary>
internal static class CountryStrings
{
    private const int Letters = 26;
    private static readonly string[] Cache = Build();

    private static string[] Build()
    {
        var cache = new string[Letters * Letters];
        for (var first = 0; first < Letters; first++)
        {
            for (var second = 0; second < Letters; second++)
            {
                cache[(first * Letters) + second] = new string([(char)('A' + first), (char)('A' + second)]);
            }
        }

        return cache;
    }

    /// <summary>The code for a packed value, or null when it is zero or not two letters A-Z.</summary>
    internal static string? Get(ushort packed)
    {
        var first = (packed >> 8) - 'A';
        var second = (packed & 0xFF) - 'A';
        return (uint)first < Letters && (uint)second < Letters
            ? Cache[(first * Letters) + second]
            : null;
    }
}
