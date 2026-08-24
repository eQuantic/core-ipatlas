using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Compiler.Tests;

public class ArgumentsTests
{
    [Fact]
    public void Reads_a_command_with_repeated_and_multi_valued_flags()
    {
        var args = Arguments.Parse(["build", "--rir", "a", "b", "--out", "x.eqatlas", "--rir", "c"]);

        args.Command.ShouldBe("build");
        args.All("rir").ShouldBe(["a", "b", "c"]);
        args.One("out").ShouldBe("x.eqatlas");
        args.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void A_flag_with_no_value_is_an_error_not_a_crash()
    {
        // "--out" as the last argument used to walk off the end of the array and
        // hand the user an IndexOutOfRangeException with a stack trace.
        var args = Arguments.Parse(["build", "--rir", "a", "--out"]);

        args.One("out", required: true).ShouldBeNull();
        args.Errors.ShouldContain(message => message.Contains("--out is required"));
    }

    [Fact]
    public void Reports_a_repeated_single_value_flag()
    {
        var args = Arguments.Parse(["build", "--out", "one.eqatlas", "two.eqatlas"]);

        args.One("out");

        args.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Reports_a_stray_argument()
    {
        var args = Arguments.Parse(["build", "--out", "x", "--", "stray"]);

        args.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Presence_flags_need_no_value() =>
        Arguments.Parse(["build", "--asn-heuristics", "--out", "x"]).Has("asn-heuristics").ShouldBeTrue();
}

public class BuildCommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("eqatlas-tests").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    private static string Fixture(string name) => System.IO.Path.Combine("Fixtures", name);

    private (int Code, string Out, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = BuildCommand.Run(Arguments.Parse(["build", .. args]), output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Builds_a_dataset_every_layer_contributed_to()
    {
        var target = Path_("world.eqatlas");

        var result = Run(
            "--rir", Fixture("delegated-sample"),
            "--asn", Fixture("ip2asn-sample.tsv"),
            "--geofeed", Fixture("geofeed-sample.csv"),
            "--cloud", Fixture("aws-sample.json"), Fixture("gcp-sample.json"),
            "--out", target,
            "--built-at", "2026-08-24T00:00:00Z");

        result.Code.ShouldBe(0);
        var db = IpAtlasDatabase.Open(target);
        db.BuiltAt.ShouldBe(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));

        // Registry says the AWS block is Swedish; AWS says eu-north-1, which is
        // Stockholm, and both agree. The Korean block is where they disagree.
        db.Lookup("3.5.140.1").CountryCode.ShouldBe("KR");
        db.Lookup("3.5.140.1").IsHosting.ShouldBeTrue();
        db.Lookup("3.5.140.1").Location!.Value.City.ShouldBe("Seoul");

        // Registry delegated 34.76.0.0/16 to the US; Google runs it in Belgium.
        db.Lookup("34.76.0.1").CountryCode.ShouldBe("BE");

        // CloudFront is global: hosting and anycast, no place.
        db.Lookup("13.32.0.1").IsAnycast.ShouldBeTrue();

        // A geofeed beats the registry that delegated the block.
        db.Lookup("5.10.0.1").Location!.Value.City.ShouldBe("Amsterdam");
    }

    [Fact]
    public void Reports_the_records_it_had_to_reject()
    {
        var result = Run("--rir", Fixture("delegated-sample"), "--out", Path_("a.eqatlas"));

        result.Code.ShouldBe(0);
        result.Out.ShouldContain("out of range");
        result.Out.ShouldContain("rejected");
    }

    [Fact]
    public void A_failed_build_leaves_the_dataset_already_in_place_untouched()
    {
        // The failure that mattered most: rebuilding over a live dataset used to
        // truncate it to zero bytes before the new one was ready.
        var target = Path_("live.eqatlas");
        Run("--rir", Fixture("delegated-sample"), "--out", target).Code.ShouldBe(0);
        var good = File.ReadAllBytes(target);

        // A directory cannot be replaced by a file, so the rename fails after the
        // whole dataset has been built.
        var blocked = Path_("blocked.eqatlas");
        Directory.CreateDirectory(blocked);
        var result = Run("--rir", Fixture("delegated-sample"), "--out", blocked);

        result.Code.ShouldBe(1);
        result.Error.ShouldContain("could not write");
        File.ReadAllBytes(target).ShouldBe(good);
        Directory.EnumerateFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Refuses_a_missing_source_file_with_a_sentence()
    {
        var result = Run("--rir", "/no/such/file", "--out", Path_("x.eqatlas"));

        result.Code.ShouldBe(2);
        result.Error.ShouldContain("no such file");
        result.Error.ShouldNotContain("Exception");
    }

    [Fact]
    public void Refuses_a_build_with_no_location_source_at_all()
    {
        var result = Run("--asn", Fixture("ip2asn-sample.tsv"), "--out", Path_("x.eqatlas"));

        result.Code.ShouldBe(2);
        result.Error.ShouldContain("--rir");
    }

    [Fact]
    public void Refuses_a_cloud_file_that_is_not_json()
    {
        var junk = Path_("junk.json");
        File.WriteAllText(junk, "this is not json");

        var result = Run("--rir", Fixture("delegated-sample"), "--cloud", junk, "--out", Path_("x.eqatlas"));

        result.Code.ShouldBe(2);
        result.Error.ShouldContain("not readable JSON");
    }

    [Fact]
    public void Verify_and_lookup_read_back_what_build_wrote()
    {
        var target = Path_("world.eqatlas");
        Run("--rir", Fixture("delegated-sample"), "--cloud", Fixture("aws-sample.json"), "--out", target)
            .Code.ShouldBe(0);

        var output = new StringWriter();
        var error = new StringWriter();
        InspectCommands.Verify(Arguments.Parse(["verify", "--dataset", target]), output, error).ShouldBe(0);
        output.ToString().ShouldContain("checksum  verified");

        output = new StringWriter();
        InspectCommands
            .Lookup(Arguments.Parse(["lookup", "--dataset", target, "--ip", "3.5.140.1", "10.0.0.1"]), output, error)
            .ShouldBe(0);
        var text = output.ToString();
        text.ShouldContain("Seoul");
        text.ShouldContain("Private");
    }

    [Fact]
    public void Verify_fails_a_dataset_older_than_the_limit()
    {
        var target = Path_("stale.eqatlas");
        Run("--rir", Fixture("delegated-sample"), "--out", target, "--built-at", "2020-01-01T00:00:00Z")
            .Code.ShouldBe(0);

        var error = new StringWriter();
        var code = InspectCommands.Verify(
            Arguments.Parse(["verify", "--dataset", target, "--max-age-days", "30"]), new StringWriter(), error);

        code.ShouldBe(1);
        error.ToString().ShouldContain("days old");
    }

    [Fact]
    public void Verify_fails_a_corrupt_dataset()
    {
        var target = Path_("rot.eqatlas");
        Run("--rir", Fixture("delegated-sample"), "--out", target).Code.ShouldBe(0);
        var bytes = File.ReadAllBytes(target);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(target, bytes);

        var error = new StringWriter();
        var code = InspectCommands.Verify(Arguments.Parse(["verify", "--dataset", target]), new StringWriter(), error);

        code.ShouldBe(1);
        error.ToString().ShouldContain("corrupt");
    }

    [Fact]
    public void Every_source_in_the_fetch_catalogue_is_https_and_named_for_a_build_flag()
    {
        FetchCommand.Catalogue.ShouldNotBeEmpty();
        FetchCommand.Catalogue.ShouldAllBe(file => file.Flag.StartsWith("--", StringComparison.Ordinal));
        FetchCommand.Catalogue.ShouldAllBe(file =>
            file.Url.StartsWith("https://", StringComparison.Ordinal)
            || file.DiscoverFrom!.StartsWith("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_optional_sources_may_lack_a_fixed_url()
    {
        // A required source behind a page that can be redesigned would make every
        // scheduled rebuild depend on someone else's HTML.
        FetchCommand.Catalogue
            .Where(file => file.Url.Length == 0)
            .ShouldAllBe(file => file.Optional && file.DiscoverFrom != null);
    }
}
