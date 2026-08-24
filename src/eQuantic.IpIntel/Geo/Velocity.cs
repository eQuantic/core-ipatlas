namespace eQuantic.IpIntel.Geo;

/// <summary>The verdict on one pair of sightings.</summary>
/// <param name="Plausible">
/// True when the implied speed is humanly achievable, false when it is not —
/// and null when either side's location is unknown, because "we cannot tell"
/// must never masquerade as either answer.
/// </param>
/// <param name="DistanceKm">Great-circle distance between the sightings, when both are known.</param>
/// <param name="KilometersPerHour">The implied speed, when computable.</param>
public readonly record struct TravelAssessment(
    bool? Plausible, double? DistanceKm, double? KilometersPerHour)
{
    /// <summary>Neither side located: no verdict.</summary>
    public static readonly TravelAssessment Unknown = new(null, null, null);
}

/// <summary>
/// Impossible-travel math over country centroids: was there enough time to
/// get from where the last sign-in came from to where this one did? Airliner
/// speed is the default bar — nobody beats a Boeing commuting.
/// </summary>
public static class Velocity
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>Commercial-flight ceiling, generous on purpose.</summary>
    public const double DefaultMaxKilometersPerHour = 950.0;

    /// <summary>Judges whether the move between two sightings was humanly possible in the elapsed time.</summary>
    public static TravelAssessment Assess(
        string? fromCountry, string? toCountry, TimeSpan elapsed,
        double maxKilometersPerHour = DefaultMaxKilometersPerHour)
    {
        // Same country: centroid distance would be pure noise, and a country
        // is always crossable at the granularity this signal answers at.
        if (fromCountry is not null
            && string.Equals(fromCountry, toCountry, StringComparison.OrdinalIgnoreCase))
        {
            return new TravelAssessment(true, 0, 0);
        }

        if (CountryCentroids.Get(fromCountry) is not { } from
            || CountryCentroids.Get(toCountry) is not { } to)
        {
            return TravelAssessment.Unknown;
        }

        var distance = HaversineKm(from.Lat, from.Lon, to.Lat, to.Lon);
        if (elapsed <= TimeSpan.Zero)
        {
            return new TravelAssessment(distance < 1, distance, null);
        }

        var speed = distance / elapsed.TotalHours;
        return new TravelAssessment(speed <= maxKilometersPerHour, distance, speed);
    }

    /// <summary>Great-circle distance between two coordinates, in kilometers.</summary>
    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2))
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(a));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;
}
