using System.Net;
using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Tests;

/// <summary>
/// The writer exists so that nobody has to reimplement the format to build a
/// small dataset. These check that what it produces is what the reader expects,
/// and that it refuses to produce anything the reader would reject.
/// </summary>
public class AtlasWriterTests
{
    private static IpAtlasDatabase Write(Action<AtlasWriter> configure, string source = "test")
    {
        var writer = new AtlasWriter(source, DateTimeOffset.UnixEpoch);
        configure(writer);
        var stream = new MemoryStream();
        writer.WriteTo(stream);
        stream.Position = 0;
        return IpAtlasDatabase.Open(stream);
    }

    [Fact]
    public void A_prefix_is_the_shortest_way_to_write_a_fixture()
    {
        var db = Write(w => w.AddPrefix("45.10.0.0/24", new AtlasRecord("PT", 1930)));

        db.Lookup("45.10.0.1").CountryCode.ShouldBe("PT");
        db.Lookup("45.10.0.1").Asn.ShouldBe(1930u);
        db.Lookup("45.10.1.1").IsKnown.ShouldBeFalse();
    }

    [Fact]
    public void A_bare_address_is_a_single_host()
    {
        var db = Write(w => w.AddPrefix("45.10.0.7", new AtlasRecord("PT")));

        db.Lookup("45.10.0.7").CountryCode.ShouldBe("PT");
        db.Lookup("45.10.0.8").IsKnown.ShouldBeFalse();
    }

    [Fact]
    public void Everything_a_record_carries_survives_the_round_trip()
    {
        var db = Write(w => w.AddPrefix("45.10.0.0/24", new AtlasRecord(
            "DE", 16509, NetworkTraits.Hosting | NetworkTraits.Anycast, LocationSource.CloudProvider,
            50.11, 8.68, "eu-central-1", "Frankfurt")));

        var info = db.Lookup("45.10.0.1");

        info.CountryCode.ShouldBe("DE");
        info.Asn.ShouldBe(16509u);
        info.Traits.ShouldBe(NetworkTraits.Hosting | NetworkTraits.Anycast);
        info.Location!.Value.City.ShouldBe("Frankfurt");
        info.Location.Value.Region.ShouldBe("eu-central-1");
        info.Location.Value.Latitude!.Value.ShouldBe(50.11, 0.01);
        info.Location.Value.Source.ShouldBe(LocationSource.CloudProvider);
    }

    [Fact]
    public void A_place_shared_by_many_ranges_is_written_once()
    {
        var frankfurt = new AtlasRecord("DE", City: "Frankfurt", Latitude: 50.11, Longitude: 8.68);
        var db = Write(w =>
        {
            w.AddPrefix("45.10.0.0/24", frankfurt);
            w.AddPrefix("45.20.0.0/24", frankfurt);
            w.AddPrefix("45.30.0.0/24", frankfurt with { City = "Berlin" });
        });

        db.LocationCount.ShouldBe(2);
        db.Lookup("45.10.0.1").Location!.Value.City.ShouldBe("Frankfurt");
        db.Lookup("45.30.0.1").Location!.Value.City.ShouldBe("Berlin");
    }

    [Fact]
    public void Ranges_are_sorted_for_you()
    {
        // A caller should not have to know that the reader binary-searches.
        var db = Write(w =>
        {
            w.AddPrefix("45.30.0.0/24", new AtlasRecord("GB"));
            w.AddPrefix("45.10.0.0/24", new AtlasRecord("PT"));
            w.AddPrefix("45.20.0.0/24", new AtlasRecord("ES"));
        });

        db.Lookup("45.10.0.1").CountryCode.ShouldBe("PT");
        db.Lookup("45.20.0.1").CountryCode.ShouldBe("ES");
        db.Lookup("45.30.0.1").CountryCode.ShouldBe("GB");
    }

    [Fact]
    public void It_will_not_write_a_file_the_reader_would_reject()
    {
        // The reader refuses overlapping ranges, so the writer refuses to make
        // them — otherwise the failure surfaces at load, far from the cause.
        var writer = new AtlasWriter("test", DateTimeOffset.UnixEpoch);
        writer.AddPrefix("45.10.0.0/16", new AtlasRecord("PT"));
        writer.AddPrefix("45.10.5.0/24", new AtlasRecord("ES"));

        var thrown = Should.Throw<InvalidOperationException>(() => writer.WriteTo(new MemoryStream()));

        thrown.Message.ShouldContain("overlap");
    }

    [Fact]
    public void It_refuses_a_range_that_ends_before_it_starts()
    {
        var writer = new AtlasWriter("test", DateTimeOffset.UnixEpoch);

        Should.Throw<ArgumentException>(() => writer.AddV4(0x0AFFFFFF, 0x0A000000, new AtlasRecord("PT")));
    }

    [Fact]
    public void A_record_that_says_nothing_is_not_written()
    {
        var db = Write(w =>
        {
            w.AddPrefix("45.10.0.0/24", default);
            w.AddPrefix("45.20.0.0/24", new AtlasRecord("PT"));
        });

        db.V4RangeCount.ShouldBe(1);
    }

    [Fact]
    public void Both_families_and_addresses_as_well_as_prefixes()
    {
        var db = Write(w =>
        {
            w.Add(IPAddress.Parse("45.10.0.0"), IPAddress.Parse("45.10.0.255"), new AtlasRecord("PT"));
            w.AddPrefix("2a01:4f8::/32", new AtlasRecord("DE", 24940));
        });

        db.V4RangeCount.ShouldBe(1);
        db.V6RangeCount.ShouldBe(1);
        db.Lookup("2a01:4f8::1").Asn.ShouldBe(24940u);
    }

    [Fact]
    public void Nonsense_prefixes_are_refused_rather_than_thrown_over()
    {
        var writer = new AtlasWriter("test", DateTimeOffset.UnixEpoch);

        writer.AddPrefix("not a prefix", new AtlasRecord("PT")).ShouldBeFalse();
        writer.AddPrefix("45.10.0.0/33", new AtlasRecord("PT")).ShouldBeFalse();
        writer.V4Count.ShouldBe(0);
    }

    [Fact]
    public void The_header_carries_what_it_was_told()
    {
        var built = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        var writer = new AtlasWriter("a fixture", built);
        writer.AddPrefix("45.10.0.0/24", new AtlasRecord("PT"));
        var stream = new MemoryStream();
        writer.WriteTo(stream);
        stream.Position = 0;

        var db = IpAtlasDatabase.Open(stream);

        db.Source.ShouldBe("a fixture");
        db.BuiltAt.ShouldBe(built);
        db.FormatVersion.ShouldBe(2);
    }

    [Fact]
    public void The_same_writes_produce_the_same_bytes()
    {
        static byte[] Once()
        {
            var writer = new AtlasWriter("fixed", DateTimeOffset.UnixEpoch);
            writer.AddPrefix("45.20.0.0/24", new AtlasRecord("ES", City: "Madrid"));
            writer.AddPrefix("45.10.0.0/24", new AtlasRecord("PT", City: "Lisboa"));
            var stream = new MemoryStream();
            writer.WriteTo(stream);
            return stream.ToArray();
        }

        Once().ShouldBe(Once());
    }

    [Fact]
    public void An_empty_dataset_is_valid()
    {
        var db = Write(_ => { });

        db.V4RangeCount.ShouldBe(0);
        db.Lookup("8.8.8.8").IsKnown.ShouldBeFalse();
    }
}
