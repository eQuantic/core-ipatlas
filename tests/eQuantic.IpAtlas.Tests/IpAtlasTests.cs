using eQuantic.IpAtlas;
using Xunit;
using eQuantic.IpAtlas.Compiler;
using eQuantic.IpAtlas.Geo;
using Shouldly;

namespace eQuantic.IpAtlas.Tests;

public class RirDelegatedParserTests
{
    private static IReadOnlyList<CountryRange> ParseFixture()
    {
        using var reader = new StreamReader(Path.Combine("Fixtures", "delegated-sample"));
        return RirDelegatedParser.Parse(reader).ToList();
    }

    [Fact]
    public void Parses_allocated_and_assigned_records_only()
    {
        var ranges = ParseFixture();

        // 3 ipv4 + 2 ipv6; the reserved status and the '*' country are skipped.
        ranges.Count.ShouldBe(5);
        ranges.Count(r => r.IsV6).ShouldBe(2);
    }

    [Fact]
    public void Ipv4_value_is_an_address_count()
    {
        var fr = ParseFixture().Single(r => r.CountryCode == "FR");

        // 2.0.0.0 + 1048576 addresses = up to 2.15.255.255.
        fr.Start.ShouldBe((UInt128)0x02000000);
        fr.End.ShouldBe((UInt128)0x020FFFFF);
    }

    [Fact]
    public void Ipv6_value_is_a_prefix_length()
    {
        var de = ParseFixture().Single(r => r.CountryCode == "DE");

        var expectedSize = UInt128.One << (128 - 29);
        (de.End - de.Start + 1).ShouldBe(expectedSize);
    }
}

public class DatasetRoundTripTests
{
    private static IpAtlasDatabase BuildFromFixtures()
    {
        var builder = new DatasetBuilder();
        using (var rir = new StreamReader(Path.Combine("Fixtures", "delegated-sample")))
        {
            builder.AddCountries(RirDelegatedParser.Parse(rir));
        }

        using (var asn = new StreamReader(Path.Combine("Fixtures", "ip2asn-sample.tsv")))
        {
            builder.AddAsns(AsnTsvParser.Parse(asn));
        }

        var stream = new MemoryStream();
        builder.Write(stream, "fixtures", new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));
        stream.Position = 0;
        return IpAtlasDatabase.Open(stream);
    }

    [Fact]
    public void Looks_up_country_and_asn_for_v4()
    {
        var db = BuildFromFixtures();

        var inFrance = db.Lookup("2.0.4.4");
        inFrance.CountryCode.ShouldBe("FR");
        inFrance.Asn.ShouldBe(3215u);

        // Past the ASN range but inside the country delegation.
        var frNoAsn = db.Lookup("2.4.0.1");
        frNoAsn.CountryCode.ShouldBe("FR");
        frNoAsn.Asn.ShouldBeNull();

        // The ASN range starts 4 addresses into the GB block.
        db.Lookup("2.16.0.1").Asn.ShouldBeNull();
        db.Lookup("2.16.0.10").Asn.ShouldBe(20940u);
        db.Lookup("2.16.0.10").CountryCode.ShouldBe("GB");
    }

    [Fact]
    public void Looks_up_v6_and_maps_v4_mapped_addresses()
    {
        var db = BuildFromFixtures();

        var hetzner = db.Lookup("2a01:4f8::1");
        hetzner.CountryCode.ShouldBe("DE");
        hetzner.Asn.ShouldBe(24940u);

        db.Lookup("2001:8a0::1").CountryCode.ShouldBe("PT");
        db.Lookup("::ffff:2.0.0.1").CountryCode.ShouldBe("FR");
    }

    [Fact]
    public void Unknown_addresses_answer_unknown()
    {
        var db = BuildFromFixtures();

        db.Lookup("9.9.9.9").IsKnown.ShouldBeFalse();
        db.Lookup("192.168.1.1").IsKnown.ShouldBeFalse();
        db.Lookup("not an ip").IsKnown.ShouldBeFalse();
        db.Lookup("fe80::1").IsKnown.ShouldBeFalse();
    }

    [Fact]
    public void Header_metadata_survives_the_trip()
    {
        var db = BuildFromFixtures();

        db.Source.ShouldBe("fixtures");
        db.BuiltAt.ShouldBe(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));
        db.V4RangeCount.ShouldBeGreaterThan(0);
        db.V6RangeCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Not_routed_asn_zero_is_skipped()
    {
        var db = BuildFromFixtures();

        // The NL block appears in the ASN file with AS0: country stays, no ASN.
        var nl = db.Lookup("5.10.0.1");
        nl.CountryCode.ShouldBe("NL");
        nl.Asn.ShouldBeNull();
    }
}

public class VelocityTests
{
    [Fact]
    public void Same_country_is_always_plausible()
    {
        var verdict = Velocity.Assess("BR", "br", TimeSpan.FromSeconds(1));

        verdict.Plausible.ShouldBe(true);
    }

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
    public void Unknown_countries_refuse_to_answer()
    {
        Velocity.Assess(null, "PT", TimeSpan.FromHours(1)).Plausible.ShouldBeNull();
        Velocity.Assess("PT", "ZZ", TimeSpan.FromHours(1)).Plausible.ShouldBeNull();
    }

    [Fact]
    public void Neighbours_within_an_hour_are_fine()
    {
        Velocity.Assess("PT", "ES", TimeSpan.FromHours(1)).Plausible.ShouldBe(true);
    }

    [Fact]
    public void Haversine_matches_known_distances()
    {
        // Lisbon (38.72, -9.14) to Madrid (40.42, -3.70) is ~500 km.
        var km = Velocity.HaversineKm(38.72, -9.14, 40.42, -3.70);

        km.ShouldBeInRange(480, 520);
    }
}
