namespace eQuantic.IpAtlas;

/// <summary>
/// Where a range actually is, when a source better than a registry delegation
/// said so. Coordinates are the point: country centroids answer "which country"
/// questions, but a datacenter's real coordinates answer distance questions,
/// and distance is what a travel-velocity signal is made of.
/// </summary>
/// <param name="Latitude">Degrees north, when the source carried coordinates.</param>
/// <param name="Longitude">Degrees east, when the source carried coordinates.</param>
/// <param name="Region">Subdivision name or code, when the source carried one.</param>
/// <param name="City">City name, when the source carried one.</param>
/// <param name="Source">Which kind of source this came from.</param>
public readonly record struct IpLocation(
    double? Latitude,
    double? Longitude,
    string? Region,
    string? City,
    LocationSource Source)
{
    /// <summary>Whether this location can answer a distance question on its own.</summary>
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
}
