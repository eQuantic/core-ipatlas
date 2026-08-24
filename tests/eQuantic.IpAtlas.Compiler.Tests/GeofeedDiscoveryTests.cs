using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Compiler.Tests;

public class WhoisGeofeedIndexTests
{
    private static List<GeofeedReference> ParseFixture() =>
        WhoisGeofeedIndex.ParseFile(Path.Combine("Fixtures", "whois-sample.db")).ToList();

    [Fact]
    public void Finds_the_geofeed_attribute()
    {
        var reference = ParseFixture().First(entry => entry.Range.Start == 0x2D0A0000);

        reference.Url.ShouldBe("https://example.test/geofeed.csv");
        reference.Range.End.ShouldBe((UInt128)0x2D0A00FF);
    }

    [Fact]
    public void Finds_the_older_remarks_convention()
    {
        // Plenty of objects predate RFC 9092 and still say it this way.
        ParseFixture().ShouldContain(entry =>
            entry.Range.Start == 0x2D0A0100 && entry.Url == "https://example.test/geofeed.csv");
    }

    [Fact]
    public void Reads_both_shapes_registries_write_ranges_in()
    {
        var cidr = WhoisGeofeedIndex.ParseRange("45.30.0.0/22")!.Value;
        var span = WhoisGeofeedIndex.ParseRange("45.10.0.0 - 45.10.0.255")!.Value;

        cidr.Start.ShouldBe((UInt128)0x2D1E0000);
        cidr.End.ShouldBe((UInt128)0x2D1E03FF);
        span.Start.ShouldBe((UInt128)0x2D0A0000);
        span.End.ShouldBe((UInt128)0x2D0A00FF);
    }

    [Fact]
    public void Reads_ipv6_objects() =>
        ParseFixture().ShouldContain(entry => entry.Range.IsV6 && entry.Url == "https://example.test/v6.csv");

    [Fact]
    public void Ignores_objects_with_no_geofeed() =>
        ParseFixture().ShouldNotContain(entry => entry.Range.Start == 0x2D140000);

    [Fact]
    public void Ignores_schemes_that_are_not_http() =>
        ParseFixture().ShouldNotContain(entry => entry.Url.StartsWith("ftp", StringComparison.Ordinal));

    [Fact]
    public void Trims_punctuation_someone_typed_after_the_url() =>
        ParseFixture().ShouldContain(entry => entry.Url == "https://example.test/trailing.csv");

    [Fact]
    public void Reads_a_gzipped_dump_by_sniffing_the_magic()
    {
        // Registries publish these gzipped and people gunzip them; the extension
        // says nothing reliable either way.
        var plain = File.ReadAllBytes(Path.Combine("Fixtures", "whois-sample.db"));
        var path = Path.Combine(Path.GetTempPath(), $"whois-{Guid.NewGuid():N}.db");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress))
            {
                gzip.Write(plain);
            }

            WhoisGeofeedIndex.ParseFile(path).Count().ShouldBe(ParseFixture().Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// The check that decides whether importing geofeeds is safe. A geofeed is a
/// file on a web server; without this, publishing one would be enough to
/// relocate anybody's addresses.
/// </summary>
public class GeofeedAuthorizationTests
{
    private static GeofeedAuthorization For(params string[] prefixes)
    {
        var authorization = new GeofeedAuthorization();
        foreach (var prefix in prefixes)
        {
            authorization.Allow(AtlasEntry.FromPrefix(prefix)!.Value);
        }

        return authorization.Compact();
    }

    [Fact]
    public void Accepts_a_prefix_inside_a_range_that_referenced_the_feed() =>
        For("45.10.0.0/16").Covers(AtlasEntry.FromPrefix("45.10.5.0/24")!.Value).ShouldBeTrue();

    [Fact]
    public void Accepts_the_authorising_range_itself() =>
        For("45.10.0.0/16").Covers(AtlasEntry.FromPrefix("45.10.0.0/16")!.Value).ShouldBeTrue();

    [Fact]
    public void Refuses_a_prefix_the_publisher_has_no_registry_object_for()
    {
        // The whole point: a feed at any URL claiming someone else's space.
        For("45.10.0.0/16").Covers(AtlasEntry.FromPrefix("8.8.8.0/24")!.Value).ShouldBeFalse();
    }

    [Fact]
    public void Refuses_a_prefix_that_only_partly_overlaps()
    {
        // Half inside is not inside. Clipping it would let a feed reach past its
        // own boundary by one bit at a time.
        For("45.10.0.0/24").Covers(AtlasEntry.FromPrefix("45.10.0.0/16")!.Value).ShouldBeFalse();
    }

    [Fact]
    public void Does_not_confuse_the_address_families()
    {
        var authorization = For("45.10.0.0/16");

        authorization.Covers(AtlasEntry.FromPrefix("2a01:4f8::/32")!.Value).ShouldBeFalse();
        For("2a01:4f8::/32").Covers(AtlasEntry.FromPrefix("2a01:4f8:1::/48")!.Value).ShouldBeTrue();
    }

    [Fact]
    public void Takes_the_union_of_every_object_that_referenced_the_feed()
    {
        // One operator, many registry objects, one file.
        var authorization = For("45.10.0.0/24", "45.20.0.0/24", "2a01:4f8::/32");

        authorization.Covers(AtlasEntry.FromPrefix("45.10.0.128/25")!.Value).ShouldBeTrue();
        authorization.Covers(AtlasEntry.FromPrefix("45.20.0.0/24")!.Value).ShouldBeTrue();
        authorization.Covers(AtlasEntry.FromPrefix("45.15.0.0/24")!.Value).ShouldBeFalse();
    }

    [Fact]
    public void Merges_adjacent_objects_into_one_range()
    {
        var authorization = For("45.10.0.0/24", "45.10.1.0/24");

        authorization.RangeCount.ShouldBe(1);
        authorization.Covers(AtlasEntry.FromPrefix("45.10.0.0/23")!.Value).ShouldBeTrue();
    }

    [Fact]
    public void An_empty_authorisation_covers_nothing() =>
        For().Covers(AtlasEntry.FromPrefix("45.10.0.0/24")!.Value).ShouldBeFalse();

    [Fact]
    public void Cannot_be_widened_after_it_is_compacted()
    {
        var authorization = For("45.10.0.0/24");

        Should.Throw<InvalidOperationException>(() =>
            authorization.Allow(AtlasEntry.FromPrefix("8.8.8.0/24")!.Value));
    }
}

public class PrefixListParserTests
{
    [Fact]
    public void Reads_bare_addresses_as_single_hosts()
    {
        // The Tor Project publishes exits one address per line, no prefix.
        using var reader = new StringReader("171.25.193.25\n2a0b:f4c2::1\n");

        var entries = PrefixListParser.Parse(reader, NetworkTraits.Anonymizer).ToList();

        entries.Count.ShouldBe(2);
        entries[0].Start.ShouldBe(entries[0].End);
        entries[0].Traits.ShouldBe(NetworkTraits.Anonymizer);
        entries[1].IsV6.ShouldBeTrue();
        entries[1].Start.ShouldBe(entries[1].End);
    }

    [Fact]
    public void Reads_prefixes_too()
    {
        using var reader = new StringReader("104.16.0.0/13\n");

        PrefixListParser.Parse(reader, NetworkTraits.Anycast).Single().End.ShouldBe((UInt128)0x6817FFFF);
    }

    [Fact]
    public void Skips_comments_and_blank_lines()
    {
        using var reader = new StringReader("# header\n\n; also a comment\n1.2.3.4\n");

        PrefixListParser.Parse(reader, NetworkTraits.Anonymizer).Count().ShouldBe(1);
    }

    [Fact]
    public void Skips_lines_that_are_not_addresses()
    {
        using var reader = new StringReader("not an address\n1.2.3.4\nalso not one\n");

        PrefixListParser.Parse(reader, NetworkTraits.Anonymizer).Count().ShouldBe(1);
    }
}

public class GeofeedAuthorizationSafetyTests
{
    [Fact]
    public void Refuses_to_answer_before_the_ranges_are_compacted()
    {
        // Unsorted, overlapping ranges would make the search answer nonsense,
        // which for a security check means quietly letting things through.
        var authorization = new GeofeedAuthorization();
        authorization.Allow(AtlasEntry.FromPrefix("45.10.0.0/24")!.Value);

        Should.Throw<InvalidOperationException>(() =>
            authorization.Covers(AtlasEntry.FromPrefix("45.10.0.0/25")!.Value));
    }
}

public class CidrRoundTripTests
{
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("45.10.0.0/24")]
    [InlineData("1.2.3.4/32")]
    [InlineData("0.0.0.0/0")]
    [InlineData("128.0.0.0/1")]
    [InlineData("2a01:4f8::/32")]
    [InlineData("2a01:4f8:1:2::/64")]
    [InlineData("2a01:4f8::1/128")]
    [InlineData("8000::/1")]
    [InlineData("::/0")]
    public void A_prefix_survives_being_written_back_out(string prefix)
    {
        // The harvest writes an RFC 8805 file that a build reads back, so this
        // round trip is the format's own contract with itself.
        var entry = AtlasEntry.FromPrefix(prefix)!.Value;

        entry.ToCidr().ShouldBe(prefix);
        var reparsed = AtlasEntry.FromPrefix(entry.ToCidr())!.Value;
        reparsed.Start.ShouldBe(entry.Start);
        reparsed.End.ShouldBe(entry.End);
    }
}
