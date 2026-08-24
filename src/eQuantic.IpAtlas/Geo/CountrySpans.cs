namespace eQuantic.IpAtlas.Geo;

/// <summary>
/// Roughly how far apart two points inside one country can be.
/// <para>
/// This exists because "same country" was quietly answering "plausible" for
/// every pair. That is fine for Belgium and wrong for Russia: two sightings ten
/// minutes apart could be one office, or they could be Kaliningrad and
/// Vladivostok. Without coordinates the honest answer is that we cannot tell,
/// and this table is what decides which of the two cases a country is in.
/// Distances are great-circle approximations of the widest span of the
/// territory, including outlying islands that share the country's code.
/// </para>
/// </summary>
public static class CountrySpans
{
    /// <summary>
    /// What a country is assumed to span when the table does not name it. Only
    /// countries wider than <see cref="IntraCountryToleranceKm"/> are listed, so
    /// anything absent is small by construction.
    /// </summary>
    public const double DefaultKm = 500.0;

    /// <summary>
    /// How wide a country can be before being "somewhere in it" stops being an
    /// unremarkable fact. Below this, two sightings in the same country carry no
    /// travel signal at all and the assessment says so; above it, the country is
    /// large enough that a centroid cannot rule anything in or out.
    /// </summary>
    public const double IntraCountryToleranceKm = 1500.0;

    /// <summary>The widest distance to assume inside a country.</summary>
    public static double Get(string? countryCode) =>
        countryCode is { Length: 2 } && Table.TryGetValue(countryCode.ToUpperInvariant(), out var span)
            ? span
            : DefaultKm;

    private static readonly Dictionary<string, double> Table = new(StringComparer.Ordinal)
    {
        ["US"] = 7500, ["RU"] = 6800, ["CA"] = 5500, ["ID"] = 5100, ["CN"] = 5000,
        ["BR"] = 4400, ["CL"] = 4300, ["AU"] = 4000, ["AR"] = 3700, ["IN"] = 3200,
        ["JP"] = 3000, ["KZ"] = 3000, ["MX"] = 3000, ["ES"] = 3000, ["GL"] = 2700,
        ["MN"] = 2400, ["MM"] = 2100, ["FR"] = 1100, ["DZ"] = 2000, ["SA"] = 2000,
        ["CD"] = 2000, ["MZ"] = 2000, ["MY"] = 2000, ["PG"] = 2000, ["IR"] = 2000,
        ["PT"] = 2000, ["PE"] = 1900, ["PH"] = 1900, ["SD"] = 1800, ["ZA"] = 1800,
        ["ML"] = 1800, ["TD"] = 1800, ["PK"] = 1800, ["TH"] = 1800, ["NO"] = 1800,
        ["LY"] = 1700, ["NE"] = 1700, ["CO"] = 1700, ["VE"] = 1700, ["VN"] = 1700,
        ["ET"] = 1600, ["TR"] = 1600, ["SE"] = 1600, ["NZ"] = 1600, ["MG"] = 1600,
        ["SO"] = 1600, ["AO"] = 1500, ["BO"] = 1500, ["EC"] = 1500, ["MR"] = 1400,
        ["NA"] = 1400, ["UZ"] = 1400, ["FI"] = 1300, ["OM"] = 1300, ["YE"] = 1300,
        ["AF"] = 1300, ["UA"] = 1300, ["IT"] = 1300, ["EG"] = 1200, ["NG"] = 1200,
        ["TZ"] = 1200, ["ZM"] = 1200,
    };
}
