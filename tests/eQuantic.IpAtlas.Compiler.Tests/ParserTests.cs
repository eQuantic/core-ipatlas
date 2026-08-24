using eQuantic.IpAtlas.Compiler;
using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Compiler.Tests;

public class RirDelegatedParserTests
{
    private static List<AtlasEntry> ParseFixture(out ParseCounters counters)
    {
        using var reader = new StreamReader(Path.Combine("Fixtures", "delegated-sample"));
        var entries = RirDelegatedParser.Parse(reader, out counters).ToList();
        return entries;
    }

    [Fact]
    public void Parses_allocated_and_assigned_records_only()
    {
        var entries = ParseFixture(out _);

        entries.ShouldAllBe(entry => entry.CountryCode != null);
        entries.ShouldNotContain(entry => entry.CountryCode == "XX"); // reserved status
        entries.Count(entry => entry.IsV6).ShouldBe(3);
    }

    [Fact]
    public void Ipv4_value_is_an_address_count()
    {
        var fr = ParseFixture(out _).Single(entry => entry.CountryCode == "FR");

        // 2.0.0.0 plus 1,048,576 addresses = up to 2.15.255.255.
        fr.Start.ShouldBe((UInt128)0x02000000);
        fr.End.ShouldBe((UInt128)0x020FFFFF);
    }

    [Fact]
    public void Ipv6_value_is_a_prefix_length()
    {
        var de = ParseFixture(out _).Single(entry => entry.CountryCode == "DE" && entry.IsV6);

        (de.End - de.Start + UInt128.One).ShouldBe(UInt128.One << (128 - 29));
    }

    [Fact]
    public void Drops_a_range_that_would_run_past_the_end_of_the_address_space()
    {
        // 255.255.255.0 with 1,024 addresses cannot exist. It used to be written
        // anyway, wrapping to a record whose end came before its start — a range
        // silently absent from a dataset that reported success.
        ParseFixture(out var counters);

        counters.OutOfRange.ShouldBe(1);
        ParseFixture(out _).ShouldNotContain(entry => entry.Start > entry.End);
    }

    [Fact]
    public void Ignores_summary_and_version_lines()
    {
        using var reader = new StringReader("2|ripencc|1|6|1|2|+0000\nripencc|*|ipv4|*|3|summary\n");

        RirDelegatedParser.Parse(reader).ShouldBeEmpty();
    }
}

public class AsnTsvParserTests
{
    private static List<AtlasEntry> Parse(bool heuristics = false)
    {
        using var reader = new StreamReader(Path.Combine("Fixtures", "ip2asn-sample.tsv"));
        return AsnTsvParser.Parse(reader, heuristics).ToList();
    }

    [Fact]
    public void Skips_not_routed_ranges() => Parse().ShouldNotContain(entry => entry.Asn == 0);

    [Fact]
    public void Reads_both_families()
    {
        Parse().ShouldContain(entry => entry.Asn == 3215 && !entry.IsV6);
        Parse().ShouldContain(entry => entry.Asn == 24940 && entry.IsV6);
    }

    [Fact]
    public void Never_takes_the_country_column()
    {
        // It is derived from the same registry data, so believing it would make
        // one source look like two.
        Parse().ShouldAllBe(entry => entry.CountryCode == null);
    }

    [Fact]
    public void Name_heuristics_are_off_unless_asked_for()
    {
        Parse(heuristics: false).ShouldAllBe(entry => entry.Flags == IpFlags.None);

        using var reader = new StringReader("1.0.0.0\t1.0.0.255\t64500\tUS\tEXAMPLE-HOSTING dedicated server\n");
        AsnTsvParser.Parse(reader, classifyFromDescription: true).Single().Flags.ShouldBe(IpFlags.Hosting);
    }
}

public class GeofeedParserTests
{
    private static List<AtlasEntry> ParseFixture()
    {
        using var reader = new StreamReader(Path.Combine("Fixtures", "geofeed-sample.csv"));
        return GeofeedParser.Parse(reader).ToList();
    }

    [Fact]
    public void Reads_prefix_country_region_and_city()
    {
        var lisbon = ParseFixture().Single(entry => entry.City == "Lisboa");

        lisbon.CountryCode.ShouldBe("PT");
        lisbon.Region.ShouldBe("PT-11");
        lisbon.Start.ShouldBe((UInt128)0xCB007100);
        lisbon.End.ShouldBe((UInt128)0xCB0071FF);
    }

    [Fact]
    public void Skips_comments_and_signature_blocks()
    {
        using var reader = new StringReader("# a comment\n-----BEGIN PGP SIGNATURE-----\n\n203.0.113.0/24,PT,,,\n");

        GeofeedParser.Parse(reader).Count().ShouldBe(1);
    }

    [Fact]
    public void A_line_that_names_no_place_is_not_a_location()
    {
        // "2.0.0.0/16,,,," is an operator declining to say. Recording it would
        // overwrite a registry's country with nothing.
        ParseFixture().ShouldNotContain(entry => entry.Start == 0x02000000);
    }

    [Fact]
    public void Reads_ipv6_feeds() =>
        ParseFixture().ShouldContain(entry => entry.IsV6 && entry.City == "Porto");
}

public class CloudRangesParserTests
{
    private static List<AtlasEntry> Parse(string fixture)
    {
        using var stream = File.OpenRead(Path.Combine("Fixtures", fixture));
        return CloudRangesParser.Parse(stream).ToList();
    }

    [Fact]
    public void Locates_aws_prefixes_by_region()
    {
        var frankfurt = Parse("aws-sample.json").First(entry => entry.Start == 0x02100000);

        frankfurt.CountryCode.ShouldBe("DE");
        frankfurt.City.ShouldBe("Frankfurt");
        frankfurt.Region.ShouldBe("eu-central-1");
        frankfurt.Latitude!.Value.ShouldBe(50.11, 0.01);
        frankfurt.Flags.ShouldBe(IpFlags.Hosting);
    }

    [Fact]
    public void Locates_google_prefixes_by_scope()
    {
        var belgium = Parse("gcp-sample.json").First(entry => entry.CountryCode == "BE");

        belgium.City.ShouldBe("Saint-Ghislain");
        belgium.Flags.ShouldBe(IpFlags.Hosting);
    }

    [Fact]
    public void Global_ranges_are_hosting_and_anycast_but_nowhere()
    {
        var cloudfront = Parse("aws-sample.json").First(entry => entry.Start == 0x0D200000);

        cloudfront.Flags.ShouldBe(IpFlags.Hosting | IpFlags.Anycast);
        cloudfront.CountryCode.ShouldBeNull();
        cloudfront.Latitude.ShouldBeNull();
    }

    [Fact]
    public void An_unknown_region_still_earns_the_hosting_flag()
    {
        // A region the table has not learned yet is still a datacenter.
        var unknown = Parse("aws-sample.json").First(entry => entry.Start == 0x09090900);

        unknown.Flags.ShouldBe(IpFlags.Hosting);
        unknown.CountryCode.ShouldBeNull();
    }

    [Fact]
    public void Reads_ipv6_prefixes() =>
        Parse("aws-sample.json").ShouldContain(entry => entry.IsV6 && entry.CountryCode == "IE");

    [Fact]
    public void Reads_azure_service_tags()
    {
        using var stream = new MemoryStream("""
        {"values":[{"name":"AzureCloud.westeurope","properties":{"region":"westeurope","addressPrefixes":["20.0.0.0/16"]}}]}
        """u8.ToArray());

        var entry = CloudRangesParser.Parse(stream).Single();

        entry.CountryCode.ShouldBe("NL");
        entry.City.ShouldBe("Amsterdam");
    }

    [Fact]
    public void Reads_a_plain_cidr_list()
    {
        using var reader = new StringReader("# cloudflare\n104.16.0.0/13\n2400:cb00::/32\n");

        var entries = CloudRangesParser.ParseCidrList(reader, IpFlags.Hosting | IpFlags.Anycast).ToList();

        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(entry => entry.Flags == (IpFlags.Hosting | IpFlags.Anycast));
    }

    [Fact]
    public void Every_region_the_table_knows_names_a_real_country()
    {
        CloudRegions.Count.ShouldBeGreaterThan(100);
        CloudRegions.Get("eu-central-1")!.Value.CountryCode.ShouldBe("DE");
        CloudRegions.Get("EU-CENTRAL-1")!.Value.CountryCode.ShouldBe("DE");
        CloudRegions.Get("nope-1").ShouldBeNull();
    }
}

public class PrefixTests
{
    [Theory]
    [InlineData("10.0.0.0/8", 0x0A000000u, 0x0AFFFFFFu)]
    [InlineData("192.168.1.0/24", 0xC0A80100u, 0xC0A801FFu)]
    [InlineData("1.2.3.4/32", 0x01020304u, 0x01020304u)]
    [InlineData("0.0.0.0/0", 0x00000000u, 0xFFFFFFFFu)]
    [InlineData("192.168.1.55/24", 0xC0A80100u, 0xC0A801FFu)]
    public void Expands_ipv4_prefixes(string prefix, uint start, uint end)
    {
        var entry = AtlasEntry.FromPrefix(prefix)!.Value;

        entry.Start.ShouldBe((UInt128)start);
        entry.End.ShouldBe((UInt128)end);
    }

    [Fact]
    public void Expands_an_ipv6_default_route()
    {
        var entry = AtlasEntry.FromPrefix("::/0")!.Value;

        entry.Start.ShouldBe(UInt128.Zero);
        entry.End.ShouldBe(UInt128.MaxValue);
    }

    [Theory]
    [InlineData("not a prefix")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.0/-1")]
    public void Refuses_nonsense(string prefix) => AtlasEntry.FromPrefix(prefix).ShouldBeNull();
}
