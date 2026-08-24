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
        For("45.10.0.0/16").Covers(AtlasEntry.FromPrefix("45.10.5.0/24")!.Value).ShouldBe(Coverage.Referenced);

    [Fact]
    public void Accepts_the_authorising_range_itself() =>
        For("45.10.0.0/16").Covers(AtlasEntry.FromPrefix("45.10.0.0/16")!.Value).ShouldBe(Coverage.Referenced);

    [Fact]
    public void Refuses_a_prefix_the_publisher_has_no_registry_object_for()
    {
        // The whole point: a feed at any URL claiming someone else's space.
        For("45.10.0.0/16").Covers(AtlasEntry.FromPrefix("8.8.8.0/24")!.Value).ShouldBe(Coverage.None);
    }

    [Fact]
    public void Refuses_a_prefix_that_only_partly_overlaps()
    {
        // Half inside is not inside. Clipping it would let a feed reach past its
        // own boundary by one bit at a time.
        For("45.10.0.0/24").Covers(AtlasEntry.FromPrefix("45.10.0.0/16")!.Value).ShouldBe(Coverage.None);
    }

    [Fact]
    public void Does_not_confuse_the_address_families()
    {
        var authorization = For("45.10.0.0/16");

        authorization.Covers(AtlasEntry.FromPrefix("2a01:4f8::/32")!.Value).ShouldBe(Coverage.None);
        For("2a01:4f8::/32").Covers(AtlasEntry.FromPrefix("2a01:4f8:1::/48")!.Value).ShouldBe(Coverage.Referenced);
    }

    [Fact]
    public void Takes_the_union_of_every_object_that_referenced_the_feed()
    {
        // One operator, many registry objects, one file.
        var authorization = For("45.10.0.0/24", "45.20.0.0/24", "2a01:4f8::/32");

        authorization.Covers(AtlasEntry.FromPrefix("45.10.0.128/25")!.Value).ShouldBe(Coverage.Referenced);
        authorization.Covers(AtlasEntry.FromPrefix("45.20.0.0/24")!.Value).ShouldBe(Coverage.Referenced);
        authorization.Covers(AtlasEntry.FromPrefix("45.15.0.0/24")!.Value).ShouldBe(Coverage.None);
    }

    [Fact]
    public void Merges_adjacent_objects_into_one_range()
    {
        var authorization = For("45.10.0.0/24", "45.10.1.0/24");

        authorization.ReferencedRangeCount.ShouldBe(1);
        authorization.Covers(AtlasEntry.FromPrefix("45.10.0.0/23")!.Value).ShouldBe(Coverage.Referenced);
    }

    [Fact]
    public void An_empty_authorisation_covers_nothing() =>
        For().Covers(AtlasEntry.FromPrefix("45.10.0.0/24")!.Value).ShouldBe(Coverage.None);

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

/// <summary>
/// The harvest end to end, against feeds that behave badly on purpose. This is
/// the path that decides whose claims about the internet get believed.
/// </summary>
public class GeofeedHarvestTests
{
    private static KeyValuePair<string, GeofeedAuthorization> Feed(string url, params string[] allowed)
    {
        var authorization = new GeofeedAuthorization();
        foreach (var prefix in allowed)
        {
            authorization.Allow(AtlasEntry.FromPrefix(prefix)!.Value);
        }

        return new KeyValuePair<string, GeofeedAuthorization>(url, authorization.Compact());
    }

    private static Task<(List<AtlasEntry> Accepted, HarvestReport Report)> Harvest(
        Dictionary<string, string?> bodies,
        params KeyValuePair<string, GeofeedAuthorization>[] feeds) =>
        GeofeedsCommand.HarvestAsync(
            feeds,
            (url, _) => Task.FromResult(bodies.TryGetValue(url, out var body) ? body : null),
            concurrency: 4,
            CancellationToken.None);

    [Fact]
    public async Task Keeps_what_a_feed_is_entitled_to_say()
    {
        var (accepted, report) = await Harvest(
            new() { ["https://a.test/g.csv"] = "45.10.0.0/24,PT,PT-11,Lisboa\n" },
            Feed("https://a.test/g.csv", "45.10.0.0/16"));

        accepted.Count.ShouldBe(1);
        accepted[0].City.ShouldBe("Lisboa");
        report.Accepted.ShouldBe(1);
        report.Unauthorized.ShouldBe(0);
    }

    [Fact]
    public async Task Discards_a_feed_claiming_space_it_does_not_hold()
    {
        // A file at any URL saying Google's resolver is in Antarctica.
        var (accepted, report) = await Harvest(
            new() { ["https://evil.test/g.csv"] = "45.10.0.0/24,PT,,Lisboa\n8.8.8.0/24,AQ,,Nowhere\n" },
            Feed("https://evil.test/g.csv", "45.10.0.0/16"));

        accepted.Count.ShouldBe(1);
        accepted.ShouldNotContain(entry => entry.CountryCode == "AQ");
        report.Unauthorized.ShouldBe(1);
        report.WorstOffenders.ShouldContain(offender => offender.Url == "https://evil.test/g.csv");
    }

    [Fact]
    public async Task Names_the_feeds_that_overclaim_instead_of_only_totalling_them()
    {
        var greedy = string.Concat(Enumerable.Range(0, 50).Select(i => $"8.{i}.0.0/16,AQ,,Nowhere\n"));
        var (_, report) = await Harvest(
            new()
            {
                ["https://greedy.test/g.csv"] = greedy,
                ["https://polite.test/g.csv"] = "45.20.0.0/24,ES,,Madrid\n",
            },
            Feed("https://greedy.test/g.csv", "45.10.0.0/24"),
            Feed("https://polite.test/g.csv", "45.20.0.0/24"));

        report.Unauthorized.ShouldBe(50);
        report.WorstOffenders[0].Url.ShouldBe("https://greedy.test/g.csv");
        report.WorstOffenders[0].Rejected.ShouldBe(50);
        report.WorstOffenders.ShouldNotContain(offender => offender.Url == "https://polite.test/g.csv");
    }

    [Fact]
    public async Task Counts_a_page_that_is_not_a_geofeed_as_unreadable()
    {
        // Plenty of these URLs now serve a parked page or a redirect to one.
        var (accepted, report) = await Harvest(
            new() { ["https://gone.test/g.csv"] = "<!DOCTYPE html><html><body>Not found</body></html>" },
            Feed("https://gone.test/g.csv", "45.10.0.0/16"));

        accepted.ShouldBeEmpty();
        report.Unreadable.ShouldBe(1);
        report.Fetched.ShouldBe(0);
    }

    [Fact]
    public async Task Counts_a_feed_that_could_not_be_read()
    {
        var (accepted, report) = await Harvest(new(), Feed("https://dead.test/g.csv", "45.10.0.0/16"));

        accepted.ShouldBeEmpty();
        report.Failed.ShouldBe(1);
    }

    [Fact]
    public async Task Takes_the_union_when_many_registry_objects_point_at_one_feed()
    {
        // One operator, many allocations, one file: the common case.
        var (accepted, _) = await Harvest(
            new() { ["https://big.test/g.csv"] = "45.10.0.0/24,PT,,Lisboa\n45.20.0.0/24,PT,,Porto\n9.9.9.0/24,PT,,Nope\n" },
            Feed("https://big.test/g.csv", "45.10.0.0/24", "45.20.0.0/24"));

        accepted.Count.ShouldBe(2);
        accepted.ShouldNotContain(entry => entry.City == "Nope");
    }

    [Fact]
    public async Task Sorts_the_output_so_a_harvest_is_reproducible()
    {
        var (accepted, _) = await Harvest(
            new()
            {
                ["https://a.test/g.csv"] = "45.20.0.0/24,PT,,Porto\n2a01:4f8::/32,DE,,Berlin\n45.10.0.0/24,PT,,Lisboa\n",
            },
            Feed("https://a.test/g.csv", "45.0.0.0/8", "2a01:4f8::/32"));

        accepted.Select(entry => entry.City).ShouldBe(["Lisboa", "Porto", "Berlin"]);
    }
}

public class HarvestOutputTests
{
    [Fact]
    public async Task What_the_harvest_writes_reads_back_as_a_geofeed()
    {
        // The output is fed straight back in through --geofeed, so it has to be
        // a geofeed: five RFC 8805 fields, empty ones included.
        var authorization = new GeofeedAuthorization();
        authorization.Allow(AtlasEntry.FromPrefix("45.10.0.0/16")!.Value);
        var feeds = new[]
        {
            new KeyValuePair<string, GeofeedAuthorization>(
                "https://a.test/g.csv", authorization.Compact()),
        };

        var (accepted, _) = await GeofeedsCommand.HarvestAsync(
            feeds,
            (_, _) => Task.FromResult<string?>("45.10.0.0/24,PT,PT-11,Lisboa,1000-001\n45.10.1.0/24,PT,,,\n"),
            concurrency: 1,
            CancellationToken.None);

        var path = Path.Combine(Path.GetTempPath(), $"harvest-{Guid.NewGuid():N}.csv");
        try
        {
            await GeofeedsCommand.WriteFeedAsync(path, accepted, CancellationToken.None);
            var lines = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))
                .Where(line => line.Length > 0 && line[0] != '#')
                .ToList();

            lines.ShouldBe(["45.10.0.0/24,PT,PT-11,Lisboa,", "45.10.1.0/24,PT,,,"]);

            using var reader = new StreamReader(path);
            var reparsed = GeofeedParser.Parse(reader).ToList();
            reparsed.Count.ShouldBe(2);
            reparsed[0].City.ShouldBe("Lisboa");
            reparsed[0].Region.ShouldBe("PT-11");
            reparsed[1].CountryCode.ShouldBe("PT");
            reparsed[1].City.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// The one step beyond RFC 9092 this tool will take, and the line it still
/// will not cross. Operators annotate a handful of objects and publish a feed
/// for their whole estate; the registry knows the rest is theirs even when
/// those objects do not say so.
/// </summary>
public class SameOrganisationWideningTests
{
    private static GeofeedAuthorization Build(
        string[] referenced, string organisation, params string[] alsoHeldByThatOrganisation)
    {
        var authorization = new GeofeedAuthorization();
        foreach (var prefix in referenced)
        {
            authorization.Allow(AtlasEntry.FromPrefix(prefix)!.Value);
        }

        authorization.AllowOrganisation(organisation);
        foreach (var prefix in alsoHeldByThatOrganisation)
        {
            authorization.AllowSameOrganisation(AtlasEntry.FromPrefix(prefix)!.Value);
        }

        return authorization.Compact();
    }

    [Fact]
    public void Accepts_space_the_registry_records_against_the_same_organisation()
    {
        // Hetzner's case: a feed for the whole estate, five objects naming it.
        var authorization = Build(["5.222.0.0/15"], "ORG-EXAMPLE", "88.99.0.0/16");

        authorization.Covers(AtlasEntry.FromPrefix("88.99.1.0/24")!.Value)
            .ShouldBe(Coverage.SameOrganisation);
    }

    [Fact]
    public void Still_prefers_and_reports_a_direct_reference()
    {
        var authorization = Build(["5.222.0.0/15"], "ORG-EXAMPLE", "5.222.0.0/15", "88.99.0.0/16");

        authorization.Covers(AtlasEntry.FromPrefix("5.222.1.0/24")!.Value).ShouldBe(Coverage.Referenced);
    }

    [Fact]
    public void Still_refuses_space_no_object_of_theirs_covers()
    {
        // The line that matters: widening follows the registry, not the feed.
        var authorization = Build(["5.222.0.0/15"], "ORG-EXAMPLE", "88.99.0.0/16");

        authorization.Covers(AtlasEntry.FromPrefix("8.8.8.0/24")!.Value).ShouldBe(Coverage.None);
    }

    [Fact]
    public void Widening_is_off_unless_organisation_ranges_were_supplied()
    {
        var authorization = Build(["5.222.0.0/15"], "ORG-EXAMPLE");

        authorization.Covers(AtlasEntry.FromPrefix("88.99.1.0/24")!.Value).ShouldBe(Coverage.None);
    }

    [Fact]
    public void An_object_with_no_org_handle_contributes_nothing_to_widen_from()
    {
        var authorization = new GeofeedAuthorization();
        authorization.Allow(AtlasEntry.FromPrefix("5.222.0.0/15")!.Value);
        authorization.AllowOrganisation(null);
        authorization.AllowOrganisation("   ");

        authorization.Organisations.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_harvest_counts_widened_acceptances_separately()
    {
        var authorization = new GeofeedAuthorization();
        authorization.Allow(AtlasEntry.FromPrefix("45.10.0.0/24")!.Value);
        authorization.AllowSameOrganisation(AtlasEntry.FromPrefix("88.99.0.0/16")!.Value);
        var feeds = new[]
        {
            new KeyValuePair<string, GeofeedAuthorization>("https://a.test/g.csv", authorization.Compact()),
        };

        var (accepted, report) = await GeofeedsCommand.HarvestAsync(
            feeds,
            (_, _) => Task.FromResult<string?>(
                "45.10.0.0/24,DE,,Falkenstein,\n88.99.1.0/24,DE,,Nuremberg,\n8.8.8.0/24,AQ,,Nowhere,\n"),
            concurrency: 1,
            CancellationToken.None);

        accepted.Count.ShouldBe(2);
        report.Widened.ShouldBe(1);
        report.Unauthorized.ShouldBe(1);
    }
}

public class HarvestDeterminismTests
{
    private static KeyValuePair<string, GeofeedAuthorization> Feed(string url, params string[] allowed)
    {
        var authorization = new GeofeedAuthorization();
        foreach (var prefix in allowed)
        {
            authorization.Allow(AtlasEntry.FromPrefix(prefix)!.Value);
        }

        return new KeyValuePair<string, GeofeedAuthorization>(url, authorization.Compact());
    }

    [Fact]
    public async Task Two_harvests_of_the_same_feeds_agree_line_for_line()
    {
        // The output is fed back into a build, so a harvest that reorders itself
        // between runs makes every downstream dataset differ for no reason. It
        // used to: ties on the range start were broken by whichever parallel
        // fetch finished first.
        var bodies = new Dictionary<string, string?>
        {
            // Same start, different lengths and places: exactly the tie that used to wobble.
            ["https://a.test/g.csv"] = "45.10.0.0/23,PT,PT-11,Lisboa,\n45.10.0.0/24,PT,PT-13,Porto,\n",
            ["https://b.test/g.csv"] = "45.20.0.0/24,ES,ES-MD,Madrid,\n",
            ["https://c.test/g.csv"] = "45.30.0.0/24,FR,FR-IDF,Paris,\n",
        };

        var feeds = new[]
        {
            Feed("https://a.test/g.csv", "45.10.0.0/16"),
            Feed("https://b.test/g.csv", "45.20.0.0/16"),
            Feed("https://c.test/g.csv", "45.30.0.0/16"),
        };

        var runs = new List<string>();
        for (var run = 0; run < 6; run++)
        {
            var delay = run;
            var (accepted, _) = await GeofeedsCommand.HarvestAsync(
                feeds,
                async (url, token) =>
                {
                    // Vary which feed lands first, the way a real network does.
                    await Task.Delay(Math.Abs(url.GetHashCode(StringComparison.Ordinal) + delay) % 7, token);
                    return bodies[url];
                },
                concurrency: 4,
                CancellationToken.None);

            runs.Add(string.Join('\n', accepted.Select(e => $"{e.ToCidr()},{e.CountryCode},{e.Region},{e.City}")));
        }

        runs.Distinct().Count().ShouldBe(1);
    }
}
