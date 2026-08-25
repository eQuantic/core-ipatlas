using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Compiler.Tests;

/// <summary>
/// RDAP is how the geofeeds of two whole registries become reachable, and the
/// reference is not where RFC 9092 says it is. These pin down where it actually
/// is, using a response trimmed from a real ARIN answer.
/// </summary>
public class RdapGeofeedReaderTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine("Fixtures", "rdap-arin-sample.json"));

    [Fact]
    public void Finds_a_geofeed_buried_in_the_organisation_entity()
    {
        // ARIN does not carry this on the network object. It is free text in the
        // remarks of the entity the network belongs to, which is why the reader
        // walks the document instead of reading a field.
        var answer = RdapGeofeedReader.Read(Fixture());

        answer.Urls.ShouldBe(["https://geofeed.cogentco.com/geofeed.csv"]);
    }

    [Fact]
    public void Reads_the_range_from_the_cidr_list()
    {
        var answer = RdapGeofeedReader.Read(Fixture());

        answer.Range.ShouldNotBeNull();
        answer.Range!.Value.ToCidr().ShouldBe("38.0.0.0/8");
    }

    [Fact]
    public void Falls_back_to_the_address_bounds_when_there_is_no_cidr_list()
    {
        var answer = RdapGeofeedReader.Read("""
        {"handle":"NET-1","startAddress":"45.10.0.0","endAddress":"45.10.0.255","entities":[]}
        """);

        answer.Range!.Value.ToCidr().ShouldBe("45.10.0.0/24");
    }

    [Fact]
    public void Reports_the_organisation_handle()
    {
        RdapGeofeedReader.Read(Fixture()).Organisation.ShouldBe("COGC");
    }

    [Fact]
    public void Ignores_urls_that_are_not_geofeeds()
    {
        var answer = RdapGeofeedReader.Read("""
        {"handle":"NET-1","startAddress":"45.10.0.0","endAddress":"45.10.0.255",
         "links":[{"rel":"self","href":"https://rdap.arin.net/registry/ip/45.10.0.0"}],
         "remarks":[{"description":["See https://example.test/terms.html"]}]}
        """);

        answer.Urls.ShouldBeEmpty();
    }

    [Fact]
    public void Ignores_a_scheme_that_is_not_http()
    {
        var answer = RdapGeofeedReader.Read("""
        {"handle":"NET-1","remarks":[{"description":["Geofeed ftp://example.test/geofeed.csv"]}]}
        """);

        answer.Urls.ShouldBeEmpty();
    }

    [Fact]
    public void Trims_punctuation_after_the_url()
    {
        var answer = RdapGeofeedReader.Read("""
        {"handle":"NET-1","remarks":[{"description":["Geofeed https://example.test/geofeed.csv."]}]}
        """);

        answer.Urls.ShouldBe(["https://example.test/geofeed.csv"]);
    }

    [Fact]
    public void Finds_the_same_url_once_however_many_times_it_appears()
    {
        var answer = RdapGeofeedReader.Read("""
        {"handle":"NET-1",
         "remarks":[{"description":["Geofeed https://example.test/geofeed.csv"]}],
         "entities":[{"handle":"ORG","remarks":[{"description":["Geofeed https://example.test/geofeed.csv"]}]}]}
        """);

        answer.Urls.Count.ShouldBe(1);
    }

    [Fact]
    public void A_response_with_nothing_useful_answers_empty()
    {
        var answer = RdapGeofeedReader.Read("""{"errorCode":404,"title":"Not Found"}""");

        answer.Range.ShouldBeNull();
        answer.Urls.ShouldBeEmpty();
        answer.Organisation.ShouldBeNull();
    }

    [Fact]
    public void Malformed_json_is_the_caller_problem_not_a_silent_empty()
    {
        // A registry returning an HTML error page should be counted as a failed
        // query, not read as a network with no geofeed.
        Should.Throw<System.Text.Json.JsonException>(() => RdapGeofeedReader.Read("<html>oops</html>"));
    }
}

public class RdapRobustnessTests
{
    [Fact]
    public void A_prefix_length_written_as_a_string_is_read_not_thrown_over()
    {
        // A registry types this as a string. JsonElement.TryGetInt32 throws on a
        // non-number rather than answering false, and the escaping exception
        // killed a 122,091-query crawl at minute forty-six.
        var answer = RdapGeofeedReader.Read("""
        {"handle":"NET-1","cidr0_cidrs":[{"v4prefix":"45.10.0.0","length":"24"}],
         "remarks":[{"description":["Geofeed https://example.test/geofeed.csv"]}]}
        """);

        answer.Range!.Value.ToCidr().ShouldBe("45.10.0.0/24");
        answer.Urls.ShouldBe(["https://example.test/geofeed.csv"]);
    }

    [Theory]
    [InlineData("""{"cidr0_cidrs":[{"v4prefix":"45.10.0.0","length":true}]}""")]
    [InlineData("""{"cidr0_cidrs":[{"v4prefix":"45.10.0.0","length":null}]}""")]
    [InlineData("""{"cidr0_cidrs":[{"v4prefix":"45.10.0.0","length":"not a number"}]}""")]
    [InlineData("""{"cidr0_cidrs":"not an array"}""")]
    [InlineData("""{"cidr0_cidrs":[42]}""")]
    public void Any_shape_a_registry_might_send_is_survivable(string json)
    {
        // No assertion beyond this: it must not throw.
        RdapGeofeedReader.Read(json);
    }

    [Fact]
    public void Falls_back_to_the_address_bounds_when_the_cidr_list_is_unusable()
    {
        var answer = RdapGeofeedReader.Read("""
        {"cidr0_cidrs":[{"v4prefix":"45.10.0.0","length":"nonsense"}],
         "startAddress":"45.10.0.0","endAddress":"45.10.0.255"}
        """);

        answer.Range!.Value.ToCidr().ShouldBe("45.10.0.0/24");
    }
}

/// <summary>
/// Resuming means appending to a file a killed process wrote. It does not stop
/// between lines.
/// </summary>
public class AppendSafelyTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), $"append-{Guid.NewGuid():N}.csv");

    [Fact]
    public void A_fragment_left_by_a_kill_does_not_join_the_next_record()
    {
        // This is the row a real crawl produced: "lac" was on disk with no
        // newline, the resume appended "registro.br/..." and the file recorded
        // "lacregistro.br/24.152.12.0" — a block belonging to neither registry.
        var path = Temp();
        try
        {
            File.WriteAllText(path, "arin/1.0.0.0,1.0.0.0/24,,arin:AAA\nlac");

            using (var writer = AppendSafely.Open(path))
            {
                writer.WriteLine("registro.br/24.152.12.0,24.152.12.0/22,,registro.br:123");
            }

            var lines = File.ReadAllLines(path);

            lines.Length.ShouldBe(2);
            lines[1].ShouldStartWith("registro.br/");
            lines.ShouldNotContain(line => line.StartsWith("lacregistro", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_ends_cleanly_is_left_alone()
    {
        var path = Temp();
        try
        {
            File.WriteAllText(path, "arin/1.0.0.0,1.0.0.0/24,,arin:AAA\n");
            AppendSafely.TrimPartialLine(path);

            File.ReadAllText(path).ShouldBe("arin/1.0.0.0,1.0.0.0/24,,arin:AAA\n");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_is_nothing_but_a_fragment_is_emptied()
    {
        var path = Temp();
        try
        {
            File.WriteAllText(path, "arin/1.0.0.0,1.0");
            AppendSafely.TrimPartialLine(path);

            new FileInfo(path).Length.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_fragment_longer_than_the_scan_buffer_is_still_found()
    {
        var path = Temp();
        try
        {
            File.WriteAllText(path, "good,line\n" + new string('x', 9000));
            AppendSafely.TrimPartialLine(path);

            File.ReadAllText(path).ShouldBe("good,line\n");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_absent_or_empty_file_is_not_an_error()
    {
        var path = Temp();
        AppendSafely.TrimPartialLine(path);
        File.WriteAllText(path, string.Empty);
        try
        {
            AppendSafely.TrimPartialLine(path);
            new FileInfo(path).Length.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
