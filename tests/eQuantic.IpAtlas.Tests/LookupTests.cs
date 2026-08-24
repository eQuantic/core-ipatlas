using System.Net;
using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Tests;

public class LookupTests
{
    private static IpAtlasDatabase Sample() => DatasetWriter.Open(DatasetWriter.Build(
        v4:
        [
            new(0x02000000, 0x020FFFFF, "FR", 3215),
            new(0x12000000, 0x1200FFFF, "DE", 16509, IpFlags.Hosting, LocationSource.CloudProvider, 1),
            new(0x68100000, 0x6810FFFF, null, 13335, IpFlags.Hosting | IpFlags.Anycast),
            new(0xDFFFFF00, 0xDFFFFFFF, "US"),
        ],
        v6: [new(new UInt128(0x2A0104F800000000, 0), new UInt128(0x2A0104F8FFFFFFFF, ulong.MaxValue), "DE", 24940)],
        places: [new(50.11f, 8.68f, "eu-central-1", "Frankfurt")]));

    [Fact]
    public void Answers_country_and_asn()
    {
        var info = Sample().Lookup("2.0.4.4");

        info.CountryCode.ShouldBe("FR");
        info.Asn.ShouldBe(3215u);
        info.IsKnown.ShouldBeTrue();
    }

    [Fact]
    public void Carries_flags_and_a_place()
    {
        var info = Sample().Lookup("18.0.0.1");

        info.CountryCode.ShouldBe("DE");
        info.IsHosting.ShouldBeTrue();
        info.Location.ShouldNotBeNull();
        info.Location!.Value.City.ShouldBe("Frankfurt");
        info.Location.Value.Region.ShouldBe("eu-central-1");
        info.Location.Value.Latitude!.Value.ShouldBe(50.11, 0.01);
        info.Location.Value.Source.ShouldBe(LocationSource.CloudProvider);
    }

    [Fact]
    public void An_anycast_range_is_not_a_persons_location()
    {
        var info = Sample().Lookup("104.16.0.1");

        info.IsAnycast.ShouldBeTrue();
        info.IsLocatablePerson.ShouldBeFalse();
    }

    [Fact]
    public void Handles_the_edges_of_the_address_space()
    {
        var db = Sample();

        db.Lookup("255.255.255.255").Scope.ShouldBe(IpScope.Broadcast);
        db.Lookup("240.0.0.1").Scope.ShouldBe(IpScope.Reserved);
        db.Lookup("223.255.255.255").CountryCode.ShouldBe("US"); // last public IPv4 address
        db.Lookup("223.255.255.0").CountryCode.ShouldBe("US");
        db.Lookup("1.255.255.255").IsKnown.ShouldBeFalse();
        db.Lookup("2.0.0.0").CountryCode.ShouldBe("FR");
        db.Lookup("2.15.255.255").CountryCode.ShouldBe("FR");
        db.Lookup("2.16.0.0").IsKnown.ShouldBeFalse();
    }

    [Fact]
    public void Maps_v4_mapped_v6_addresses()
    {
        Sample().Lookup("::ffff:2.0.0.1").CountryCode.ShouldBe("FR");
    }

    [Fact]
    public void Answers_v6()
    {
        var info = Sample().Lookup("2a01:4f8::1");

        info.CountryCode.ShouldBe("DE");
        info.Asn.ShouldBe(24940u);
    }

    [Fact]
    public void Unparsable_input_answers_unknown() =>
        Sample().Lookup("not an ip").IsKnown.ShouldBeFalse();

    [Fact]
    public void A_hit_allocates_nothing()
    {
        // The claim in the README, kept honest by a test. Every successful
        // lookup used to mint a fresh two-character country string.
        var db = Sample();
        var address = IPAddress.Parse("2.0.4.4");
        for (var i = 0; i < 1_000; i++)
        {
            db.Lookup(address);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            db.Lookup(address);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    [Fact]
    public void An_empty_dataset_answers_rather_than_crashes()
    {
        var db = DatasetWriter.Open(DatasetWriter.Build());

        db.V4RangeCount.ShouldBe(0);
        db.Lookup("8.8.8.8").IsKnown.ShouldBeFalse();
    }

    [Fact]
    public void Header_metadata_survives_the_trip()
    {
        var built = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        var db = DatasetWriter.Open(DatasetWriter.Build(
            v4: [new(1, 2, "PT")], source: "five registries", builtAt: built));

        db.Source.ShouldBe("five registries");
        db.BuiltAt.ShouldBe(built);
        db.Age.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Is_safe_to_read_from_many_threads()
    {
        var db = Sample();

        Parallel.For(0, 10_000, i =>
        {
            db.Lookup("2.0.4.4").CountryCode.ShouldBe("FR");
            db.Lookup("2a01:4f8::1").CountryCode.ShouldBe("DE");
        });
    }
}
