namespace eQuantic.IpAtlas.Geo;

/// <summary>How precisely a travel assessment could place the two sightings.</summary>
public enum TravelPrecision : byte
{
    /// <summary>Nothing located either side.</summary>
    None = 0,

    /// <summary>Country centroids: good for continent-scale questions, nothing finer.</summary>
    Country = 1,

    /// <summary>Real coordinates on at least one side, from a geofeed or a cloud provider.</summary>
    Coordinates = 2,
}

/// <summary>The verdict on one pair of sightings.</summary>
/// <param name="Plausible">
/// True when the implied speed is humanly achievable, false when it is not —
/// and null when the data cannot support either answer, because "we cannot
/// tell" must never masquerade as a verdict.
/// </param>
/// <param name="DistanceKm">Great-circle distance between the sightings, when both are known.</param>
/// <param name="KilometersPerHour">The implied speed, when computable.</param>
/// <param name="Precision">How precisely the two sightings could be placed.</param>
/// <param name="Reason">Why the assessment came out the way it did.</param>
public readonly record struct TravelAssessment(
    bool? Plausible,
    double? DistanceKm,
    double? KilometersPerHour,
    TravelPrecision Precision = TravelPrecision.None,
    TravelReason Reason = TravelReason.Assessed)
{
    /// <summary>Neither side located: no verdict.</summary>
    public static readonly TravelAssessment Unknown =
        new(null, null, null, TravelPrecision.None, TravelReason.NotLocated);
}

/// <summary>Why an assessment reached its verdict, so a caller can log something better than "null".</summary>
public enum TravelReason : byte
{
    /// <summary>The speed was computed and judged.</summary>
    Assessed = 0,

    /// <summary>At least one side had no location at all.</summary>
    NotLocated = 1,

    /// <summary>An address was anycast or an anonymizer, so its location is not the person's.</summary>
    NotAPersonsLocation = 2,

    /// <summary>The two sightings arrived out of order, so no speed can be computed.</summary>
    OutOfOrder = 3,

    /// <summary>Both sides are the same country and it is too large to tell without coordinates.</summary>
    CountryTooLarge = 4,
}

/// <summary>
/// Impossible-travel math: was there enough time to get from where the last
/// sign-in came from to where this one did? Airliner speed is the default bar —
/// nobody beats a Boeing commuting.
/// <para>
/// Three answers, not two. The library will say "no" only when the geometry
/// rules it out, and answers null wherever the data cannot carry the question:
/// an unlocated address, a datacenter or VPN whose location is the service's
/// rather than the user's, events that arrived out of order, or two sightings
/// in one country wide enough that a centroid tells you nothing.
/// </para>
/// </summary>
public static class Velocity
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>Commercial-flight ceiling, generous on purpose.</summary>
    public const double DefaultMaxKilometersPerHour = 950.0;

    /// <summary>Judges whether the move between two countries was possible in the elapsed time.</summary>
    public static TravelAssessment Assess(
        string? fromCountry, string? toCountry, TimeSpan elapsed,
        double maxKilometersPerHour = DefaultMaxKilometersPerHour) =>
        Assess(
            Locate(fromCountry, null),
            Locate(toCountry, null),
            fromCountry, toCountry,
            elapsed, maxKilometersPerHour);

    /// <summary>
    /// Judges two sightings using everything the dataset returned: real
    /// coordinates when a geofeed or cloud provider supplied them, the country
    /// centroid otherwise, and no verdict at all when an address is anycast or
    /// an anonymizer.
    /// </summary>
    public static TravelAssessment Assess(
        IpInfo from, IpInfo to, TimeSpan elapsed,
        double maxKilometersPerHour = DefaultMaxKilometersPerHour)
    {
        if (!from.IsLocatablePerson || !to.IsLocatablePerson)
        {
            var blocked = from.IsAnycast || from.IsAnonymizer || to.IsAnycast || to.IsAnonymizer;
            return new TravelAssessment(
                null, null, null, TravelPrecision.None,
                blocked ? TravelReason.NotAPersonsLocation : TravelReason.NotLocated);
        }

        return Assess(
            Locate(from.CountryCode, from.Location),
            Locate(to.CountryCode, to.Location),
            from.CountryCode, to.CountryCode,
            elapsed, maxKilometersPerHour);
    }

    private static TravelAssessment Assess(
        (double Lat, double Lon, TravelPrecision Precision)? from,
        (double Lat, double Lon, TravelPrecision Precision)? to,
        string? fromCountry, string? toCountry,
        TimeSpan elapsed, double maxKilometersPerHour)
    {
        // Events out of order say nothing about travel. Reading a negative
        // interval as zero time is how clock skew turns into a fraud alert.
        if (elapsed < TimeSpan.Zero)
        {
            return new TravelAssessment(null, null, null, TravelPrecision.None, TravelReason.OutOfOrder);
        }

        if (from is not { } origin || to is not { } destination)
        {
            return TravelAssessment.Unknown;
        }

        var precision = origin.Precision == TravelPrecision.Coordinates
            && destination.Precision == TravelPrecision.Coordinates
            ? TravelPrecision.Coordinates
            : TravelPrecision.Country;

        var sameCountry = fromCountry is not null
            && string.Equals(fromCountry, toCountry, StringComparison.OrdinalIgnoreCase);

        // Same country without coordinates: the centroids are identical, so the
        // distance is zero and means nothing. Two things can still be said. In a
        // country you can cross without it being remarkable, being seen twice in
        // it is not a travel event at all. In a country the width of Russia it
        // could be one office or it could be six thousand kilometres, and no
        // centroid will tell you which — so the answer is that we cannot say.
        if (sameCountry && precision != TravelPrecision.Coordinates)
        {
            var span = CountrySpans.Get(fromCountry);
            if (span <= CountrySpans.IntraCountryToleranceKm)
            {
                return new TravelAssessment(true, 0, 0, TravelPrecision.Country);
            }

            return elapsed > TimeSpan.Zero && span / elapsed.TotalHours <= maxKilometersPerHour
                ? new TravelAssessment(true, 0, 0, TravelPrecision.Country)
                : new TravelAssessment(null, null, null, TravelPrecision.Country, TravelReason.CountryTooLarge);
        }

        var distance = HaversineKm(origin.Lat, origin.Lon, destination.Lat, destination.Lon);
        if (elapsed <= TimeSpan.Zero)
        {
            return new TravelAssessment(distance < 1, distance, null, precision);
        }

        var speed = distance / elapsed.TotalHours;
        return new TravelAssessment(speed <= maxKilometersPerHour, distance, speed, precision);
    }

    private static (double Lat, double Lon, TravelPrecision Precision)? Locate(string? country, IpLocation? location)
    {
        if (location is { Latitude: { } latitude, Longitude: { } longitude })
        {
            return (latitude, longitude, TravelPrecision.Coordinates);
        }

        return CountryCentroids.Get(country) is { } centroid
            ? (centroid.Lat, centroid.Lon, TravelPrecision.Country)
            : null;
    }

    /// <summary>Great-circle distance between two coordinates, in kilometers.</summary>
    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2))
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(a));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;
}
