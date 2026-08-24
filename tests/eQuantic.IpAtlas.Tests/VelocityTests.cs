using eQuantic.IpAtlas.Geo;
using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Tests;

public class VelocityTests
{
    [Fact]
    public void An_airliner_pace_is_plausible_and_a_teleport_is_not()
    {
        // Lisbon to Tokyo is ~11,000 km: fine in 14 hours, absurd in 10 minutes.
        Velocity.Assess("PT", "JP", TimeSpan.FromHours(14)).Plausible.ShouldBe(true);

        var teleport = Velocity.Assess("PT", "JP", TimeSpan.FromMinutes(10));
        teleport.Plausible.ShouldBe(false);
        teleport.KilometersPerHour!.Value.ShouldBeGreaterThan(10_000);
    }

    [Fact]
    public void Neighbours_within_an_hour_are_fine() =>
        Velocity.Assess("PT", "ES", TimeSpan.FromHours(1)).Plausible.ShouldBe(true);

    [Fact]
    public void Unknown_countries_refuse_to_answer()
    {
        Velocity.Assess(null, "PT", TimeSpan.FromHours(1)).Plausible.ShouldBeNull();
        Velocity.Assess("PT", "ZZ", TimeSpan.FromHours(1)).Plausible.ShouldBeNull();
        Velocity.Assess("PT", "ZZ", TimeSpan.FromHours(1)).Reason.ShouldBe(TravelReason.NotLocated);
    }

    [Fact]
    public void A_small_country_is_always_crossable()
    {
        var verdict = Velocity.Assess("BE", "be", TimeSpan.FromMinutes(10));

        verdict.Plausible.ShouldBe(true);
        verdict.Precision.ShouldBe(TravelPrecision.Country);
    }

    [Fact]
    public void A_country_the_width_of_Russia_admits_it_cannot_tell()
    {
        // Kaliningrad to Vladivostok is 6,400 km and both are "RU". Answering
        // "plausible" here is a false negative in the exact case the signal is
        // for; answering "impossible" would flag everyone in Moscow.
        var short_ = Velocity.Assess("RU", "RU", TimeSpan.FromMinutes(10));
        short_.Plausible.ShouldBeNull();
        short_.Reason.ShouldBe(TravelReason.CountryTooLarge);

        // Given enough time even the worst case is reachable, so it can answer.
        Velocity.Assess("RU", "RU", TimeSpan.FromHours(12)).Plausible.ShouldBe(true);
    }

    [Fact]
    public void Events_out_of_order_are_not_impossible_travel()
    {
        // Clock skew and out-of-order delivery used to come back as "impossible".
        var verdict = Velocity.Assess("PT", "JP", TimeSpan.FromHours(-14));

        verdict.Plausible.ShouldBeNull();
        verdict.Reason.ShouldBe(TravelReason.OutOfOrder);
    }

    [Fact]
    public void Territories_the_registries_actually_use_are_locatable()
    {
        // Reunion is 9,000 km from mainland France and used to answer "unknown".
        var verdict = Velocity.Assess("RE", "FR", TimeSpan.FromMinutes(30));

        verdict.Plausible.ShouldBe(false);
        verdict.DistanceKm!.Value.ShouldBeGreaterThan(8_000);

        foreach (var code in new[] { "VG", "KY", "IM", "GI", "GG", "JE", "BM", "CW", "GL", "GF", "XK" })
        {
            CountryCentroids.Get(code).ShouldNotBeNull($"{code} should be locatable");
        }
    }

    [Fact]
    public void Region_codes_that_are_not_places_stay_unknown()
    {
        // "EU" and "AP" mean "across a region". Giving them a point would invent
        // a precision the data does not have.
        CountryCentroids.Get("EU").ShouldBeNull();
        CountryCentroids.Get("AP").ShouldBeNull();
    }

    [Fact]
    public void Uses_real_coordinates_when_the_dataset_has_them()
    {
        var frankfurt = new IpInfo("DE", 16509, IpFlags.Hosting, IpScope.Public,
            new IpLocation(50.11, 8.68, "eu-central-1", "Frankfurt", LocationSource.CloudProvider));
        var singapore = new IpInfo("SG", 16509, IpFlags.Hosting, IpScope.Public,
            new IpLocation(1.35, 103.82, "ap-southeast-1", "Singapore", LocationSource.CloudProvider));

        var verdict = Velocity.Assess(frankfurt, singapore, TimeSpan.FromMinutes(10));

        verdict.Plausible.ShouldBe(false);
        verdict.Precision.ShouldBe(TravelPrecision.Coordinates);
        verdict.DistanceKm!.Value.ShouldBeInRange(10_000, 10_800);
    }

    [Fact]
    public void Coordinates_separate_two_cities_inside_one_country()
    {
        // The pair a country centroid can never resolve.
        var newYork = new IpInfo("US", 1, IpFlags.None, IpScope.Public,
            new IpLocation(40.71, -74.01, "US-NY", "New York", LocationSource.Geofeed));
        var losAngeles = new IpInfo("US", 2, IpFlags.None, IpScope.Public,
            new IpLocation(34.05, -118.24, "US-CA", "Los Angeles", LocationSource.Geofeed));

        var verdict = Velocity.Assess(newYork, losAngeles, TimeSpan.FromMinutes(20));

        verdict.Plausible.ShouldBe(false);
        verdict.DistanceKm!.Value.ShouldBeInRange(3_800, 4_200);
    }

    [Fact]
    public void Refuses_to_judge_an_anycast_or_anonymizer_address()
    {
        // A datacenter's location is the datacenter's. Treating it as the user's
        // is how impossible-travel checks manufacture false positives.
        var anycast = new IpInfo("US", 13335, IpFlags.Anycast | IpFlags.Hosting);
        var lisbon = new IpInfo("PT", 1930);

        var verdict = Velocity.Assess(anycast, lisbon, TimeSpan.FromMinutes(10));

        verdict.Plausible.ShouldBeNull();
        verdict.Reason.ShouldBe(TravelReason.NotAPersonsLocation);

        Velocity.Assess(new IpInfo("PT", 1, IpFlags.Anonymizer), lisbon, TimeSpan.FromHours(1))
            .Reason.ShouldBe(TravelReason.NotAPersonsLocation);
    }

    [Fact]
    public void Refuses_to_judge_a_private_address()
    {
        var internalAddress = new IpInfo(null, null, IpFlags.None, IpScope.Private);

        Velocity.Assess(internalAddress, new IpInfo("PT", 1930), TimeSpan.FromHours(1))
            .Reason.ShouldBe(TravelReason.NotLocated);
    }

    [Fact]
    public void Haversine_matches_known_distances()
    {
        // Lisbon (38.72, -9.14) to Madrid (40.42, -3.70) is ~500 km.
        Velocity.HaversineKm(38.72, -9.14, 40.42, -3.70).ShouldBeInRange(480, 520);
        Velocity.HaversineKm(0, 0, 0, 0).ShouldBe(0);
        Velocity.HaversineKm(0, -180, 0, 180).ShouldBeLessThan(1);
    }

    [Fact]
    public void A_slower_ceiling_can_be_asked_for()
    {
        // A train-speed bar, for a caller who knows their users do not fly.
        Velocity.Assess("PT", "ES", TimeSpan.FromHours(1), maxKilometersPerHour: 120).Plausible.ShouldBe(false);
    }
}
