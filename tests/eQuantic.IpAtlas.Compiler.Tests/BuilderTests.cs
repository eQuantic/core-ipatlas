using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Compiler.Tests;

/// <summary>
/// The precedence rules, which are the whole reason the builder has layers.
/// Registry delegations are the floor; every source closer to the network wins.
/// </summary>
public class BuilderPrecedenceTests
{
    private static IpAtlasDatabase Build(Action<DatasetBuilder> configure)
    {
        var builder = new DatasetBuilder();
        configure(builder);
        var stream = new MemoryStream();
        builder.Write(stream, "test", DateTimeOffset.UnixEpoch);
        stream.Position = 0;
        return IpAtlasDatabase.Open(stream);
    }

    [Fact]
    public void A_cloud_region_overrules_the_registry_that_delegated_the_block()
    {
        // The single largest error in registry-derived data: AWS registers its
        // whole estate in the United States and runs it everywhere.
        var db = Build(builder => builder
            .AddRegistry([AtlasEntry.FromPrefix("18.184.0.0/15", "US")!.Value])
            .AddCloud([AtlasEntry.FromPrefix(
                "18.184.0.0/15", "DE", flags: IpFlags.Hosting,
                latitude: 50.11, longitude: 8.68, region: "eu-central-1", city: "Frankfurt")!.Value]));

        var info = db.Lookup("18.184.0.1");

        info.CountryCode.ShouldBe("DE");
        info.IsHosting.ShouldBeTrue();
        info.Location!.Value.City.ShouldBe("Frankfurt");
        info.Location.Value.Source.ShouldBe(LocationSource.CloudProvider);
    }

    [Fact]
    public void A_geofeed_overrules_the_registry_but_not_a_cloud_provider()
    {
        var db = Build(builder => builder
            .AddRegistry([AtlasEntry.FromPrefix("45.10.0.0/24", "US")!.Value])
            .AddGeofeed([AtlasEntry.FromPrefix("45.10.0.0/24", "PT", city: "Lisboa")!.Value]));

        db.Lookup("45.10.0.1").CountryCode.ShouldBe("PT");
        db.Lookup("45.10.0.1").Location!.Value.Source.ShouldBe(LocationSource.Geofeed);

        var contested = Build(builder => builder
            .AddRegistry([AtlasEntry.FromPrefix("45.10.0.0/24", "US")!.Value])
            .AddGeofeed([AtlasEntry.FromPrefix("45.10.0.0/24", "PT", city: "Lisboa")!.Value])
            .AddCloud([AtlasEntry.FromPrefix("45.10.0.0/24", "DE", city: "Frankfurt")!.Value]));

        contested.Lookup("45.10.0.1").CountryCode.ShouldBe("DE");
    }

    [Fact]
    public void An_override_outranks_every_published_source()
    {
        var db = Build(builder => builder
            .AddRegistry([AtlasEntry.FromPrefix("45.10.0.0/24", "US")!.Value])
            .AddGeofeed([AtlasEntry.FromPrefix("45.10.0.0/24", "PT")!.Value])
            .AddCloud([AtlasEntry.FromPrefix("45.10.0.0/24", "DE")!.Value])
            .AddOverrides([AtlasEntry.FromPrefix("45.10.0.0/24", "BR", city: "Sao Paulo")!.Value]));

        db.Lookup("45.10.0.1").CountryCode.ShouldBe("BR");
        db.Lookup("45.10.0.1").Location!.Value.Source.ShouldBe(LocationSource.Override);
    }

    [Fact]
    public void The_registry_still_answers_where_no_better_source_reaches()
    {
        var db = Build(builder => builder
            .AddRegistry([AtlasEntry.FromPrefix("45.10.0.0/24", "US")!.Value])
            .AddCloud([AtlasEntry.FromPrefix("45.10.0.0/26", "DE")!.Value]));

        db.Lookup("45.10.0.1").CountryCode.ShouldBe("DE");
        db.Lookup("45.10.0.200").CountryCode.ShouldBe("US");
    }

    [Fact]
    public void Flags_accumulate_across_every_layer()
    {
        // "Datacenter" and "anycast" are true whoever noticed them.
        var db = Build(builder => builder
            .AddRegistry([AtlasEntry.FromPrefix("45.10.0.0/24", "US")!.Value])
            .AddAsns([AtlasEntry.FromPrefix("45.10.0.0/24", asn: 64500, flags: IpFlags.Mobile)!.Value])
            .AddCloud([AtlasEntry.FromPrefix("45.10.0.0/24", flags: IpFlags.Hosting | IpFlags.Anycast)!.Value]));

        var info = db.Lookup("45.10.0.1");

        info.Flags.ShouldBe(IpFlags.Hosting | IpFlags.Anycast | IpFlags.Mobile);
        info.Asn.ShouldBe(64500u);
        info.CountryCode.ShouldBe("US");
    }

    [Fact]
    public void A_cloud_range_with_no_region_keeps_the_registrys_country()
    {
        // Anycast prefixes say "datacenter" without saying where. That must not
        // erase the only country anyone did state.
        var db = Build(builder => builder
            .AddRegistry([AtlasEntry.FromPrefix("104.16.0.0/13", "US")!.Value])
            .AddCloud([AtlasEntry.FromPrefix(
                "104.16.0.0/13", flags: IpFlags.Hosting | IpFlags.Anycast)!.Value]));

        var info = db.Lookup("104.16.0.1");

        info.CountryCode.ShouldBe("US");
        info.IsAnycast.ShouldBeTrue();
        info.IsLocatablePerson.ShouldBeFalse();
    }

    [Fact]
    public void Neighbouring_segments_that_agree_are_merged()
    {
        var db = Build(builder => builder.AddRegistry(
        [
            AtlasEntry.FromPrefix("45.10.0.0/24", "PT")!.Value,
            AtlasEntry.FromPrefix("45.10.1.0/24", "PT")!.Value,
        ]));

        db.V4RangeCount.ShouldBe(1);
        db.Lookup("45.10.1.1").CountryCode.ShouldBe("PT");
        db.Lookup("45.10.0.1").CountryCode.ShouldBe("PT");
    }

    [Fact]
    public void Overlaps_inside_one_source_resolve_to_the_earlier_record()
    {
        var db = Build(builder => builder.AddRegistry(
        [
            new AtlasEntry(false, 0x02000000, 0x0200FFFF, "FR"),
            new AtlasEntry(false, 0x02008000, 0x0201FFFF, "GB"),
        ]));

        db.Lookup("2.0.0.1").CountryCode.ShouldBe("FR");
        db.Lookup("2.0.128.1").CountryCode.ShouldBe("FR");
        db.Lookup("2.1.0.1").CountryCode.ShouldBe("GB");
    }

    [Fact]
    public void Segments_with_nothing_to_say_are_not_written()
    {
        var db = Build(builder => builder.AddAsns([new AtlasEntry(false, 0x02000000, 0x0200FFFF, Asn: 0)]));

        db.V4RangeCount.ShouldBe(0);
    }

    [Fact]
    public void A_place_shared_by_many_ranges_is_stored_once()
    {
        var db = Build(builder => builder.AddCloud(
        [
            AtlasEntry.FromPrefix("18.184.0.0/16", "DE", latitude: 50.11, longitude: 8.68, city: "Frankfurt")!.Value,
            AtlasEntry.FromPrefix("52.28.0.0/16", "DE", latitude: 50.11, longitude: 8.68, city: "Frankfurt")!.Value,
        ]));

        db.LocationCount.ShouldBe(1);
        db.Lookup("18.184.0.1").Location!.Value.City.ShouldBe("Frankfurt");
        db.Lookup("52.28.0.1").Location!.Value.City.ShouldBe("Frankfurt");
    }

    [Fact]
    public void Reports_where_each_countrys_answer_came_from()
    {
        var builder = new DatasetBuilder()
            .AddRegistry([AtlasEntry.FromPrefix("45.10.0.0/24", "US")!.Value])
            .AddCloud([AtlasEntry.FromPrefix("18.184.0.0/15", "DE")!.Value])
            .AddGeofeed([AtlasEntry.FromPrefix("45.20.0.0/24", "PT")!.Value]);

        var report = builder.Write(new MemoryStream(), "test", DateTimeOffset.UnixEpoch);

        report.CountryFromRegistry.ShouldBe(1);
        report.CountryFromCloud.ShouldBe(1);
        report.CountryFromGeofeed.ShouldBe(1);
    }

    [Fact]
    public void The_same_inputs_produce_the_same_bytes()
    {
        // A dataset you cannot reproduce is a dataset you cannot audit.
        static byte[] Once()
        {
            var stream = new MemoryStream();
            new DatasetBuilder()
                .AddRegistry([AtlasEntry.FromPrefix("45.10.0.0/24", "US")!.Value])
                .AddCloud([AtlasEntry.FromPrefix("18.184.0.0/15", "DE", city: "Frankfurt")!.Value])
                .Write(stream, "fixed", DateTimeOffset.UnixEpoch);
            return stream.ToArray();
        }

        Once().ShouldBe(Once());
    }

    [Fact]
    public void A_range_reaching_the_top_of_the_address_space_does_not_overflow_the_sweep()
    {
        // The cut-point sweep adds "end + 1" for every range. At the very top of
        // the space that wraps to zero, which would put the output out of order.
        var db = Build(builder => builder.AddRegistry(
        [
            new AtlasEntry(true, UInt128.MaxValue - 255, UInt128.MaxValue, "PT"),
            AtlasEntry.FromPrefix("2a01:4f8::/32", "DE")!.Value,
        ]));

        db.V6RangeCount.ShouldBe(2);
        db.Lookup("2a01:4f8::1").CountryCode.ShouldBe("DE");
    }

    [Fact]
    public void A_range_reaching_the_top_of_the_ipv4_space_does_not_overflow_the_sweep()
    {
        var db = Build(builder => builder.AddRegistry(
        [
            new AtlasEntry(false, 0xDFFFFF00, 0xFFFFFFFF, "PT"),
            AtlasEntry.FromPrefix("45.10.0.0/24", "DE")!.Value,
        ]));

        db.V4RangeCount.ShouldBe(2);
        db.Lookup("223.255.255.1").CountryCode.ShouldBe("PT");
        db.Lookup("45.10.0.1").CountryCode.ShouldBe("DE");
    }
}
