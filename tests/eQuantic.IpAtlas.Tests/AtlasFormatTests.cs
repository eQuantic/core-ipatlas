using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Tests;

public class Crc32Tests
{
    [Fact]
    public void Matches_the_standard_check_value()
    {
        // Every CRC-32 implementation agrees on this one; if ours does too, a
        // dataset's checksum can be verified with any external tool.
        Crc32.Compute("123456789"u8).ShouldBe(0xCBF43926u);
    }

    [Fact]
    public void Is_empty_for_no_bytes() => Crc32.Compute([]).ShouldBe(0u);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(1023)]
    [InlineData(4096)]
    public void Incremental_matches_one_shot(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);

        var state = Crc32.Begin();
        var at = 0;
        foreach (var chunk in new[] { 1, 3, 8, 64, 511 })
        {
            var take = Math.Min(chunk, data.Length - at);
            state = Crc32.Update(state, data.AsSpan(at, take));
            at += take;
        }

        state = Crc32.Update(state, data.AsSpan(at));
        Crc32.Finish(state).ShouldBe(Crc32.Compute(data));
    }

    [Fact]
    public void Notices_a_single_flipped_bit()
    {
        var data = new byte[4096];
        new Random(11).NextBytes(data);
        var original = Crc32.Compute(data);

        data[2000] ^= 0x01;

        Crc32.Compute(data).ShouldNotBe(original);
    }
}

public class CountryPackingTests
{
    [Theory]
    [InlineData("PT")]
    [InlineData("br")]
    [InlineData("Gb")]
    public void Round_trips_real_codes(string code) =>
        AtlasFormat.UnpackCountry(AtlasFormat.PackCountry(code)).ShouldBe(code.ToUpperInvariant());

    [Theory]
    [InlineData("é1")]
    [InlineData("Ωx")]
    [InlineData("12")]
    [InlineData("P1")]
    [InlineData("P")]
    [InlineData("PRT")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_anything_that_is_not_two_letters(string? code)
    {
        // Packing used to accept any two characters and hand back mojibake as a
        // country code. Junk in a source file must not become a junk answer.
        AtlasFormat.PackCountry(code).ShouldBe((ushort)0);
        AtlasFormat.UnpackCountry(AtlasFormat.PackCountry(code)).ShouldBeNull();
    }

    [Fact]
    public void Hands_back_the_same_instance_every_time()
    {
        // The interned table is what keeps a lookup from allocating.
        var first = AtlasFormat.UnpackCountry(AtlasFormat.PackCountry("PT"));
        var second = AtlasFormat.UnpackCountry(AtlasFormat.PackCountry("pt"));

        ReferenceEquals(first, second).ShouldBeTrue();
    }
}
