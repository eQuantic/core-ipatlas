namespace eQuantic.IpAtlas;

/// <summary>
/// What a dataset records about one range, without the range itself. This is
/// the shape <see cref="AtlasWriter"/> takes; on the way back out it becomes an
/// <see cref="IpInfo"/>.
/// </summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2. Anything that is not two letters A-Z is stored as no country.</param>
/// <param name="Asn">Autonomous system number, or zero for none.</param>
/// <param name="Traits">What kind of network this is.</param>
/// <param name="LocationSource">Where the location came from, when there is one.</param>
/// <param name="Latitude">Degrees north, when known.</param>
/// <param name="Longitude">Degrees east, when known.</param>
/// <param name="Region">Subdivision name or code, when known.</param>
/// <param name="City">City name, when known.</param>
public readonly record struct AtlasRecord(
    string? CountryCode = null,
    uint Asn = 0,
    NetworkTraits Traits = NetworkTraits.None,
    LocationSource LocationSource = LocationSource.None,
    double? Latitude = null,
    double? Longitude = null,
    string? Region = null,
    string? City = null)
{
    /// <summary>Whether the record places the range anywhere more precisely than a country.</summary>
    public bool HasPlace => Latitude is not null || Region is not null || City is not null;

    /// <summary>Whether the record says nothing at all, and so is not worth a range.</summary>
    public bool IsEmpty =>
        AtlasFormat.PackCountry(CountryCode) == 0 && Asn == 0 && Traits == NetworkTraits.None && !HasPlace;
}
