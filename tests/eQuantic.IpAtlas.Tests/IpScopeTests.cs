using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Tests;

/// <summary>
/// Special-purpose addresses are never in a dataset. What matters is that the
/// library says so, instead of answering the same "unknown" it gives for a
/// public address it happens not to cover.
/// </summary>
public class IpScopeTests
{
    [Theory]
    [InlineData("0.0.0.0", IpScope.Unspecified)]
    [InlineData("10.0.0.5", IpScope.Private)]
    [InlineData("172.16.0.1", IpScope.Private)]
    [InlineData("172.31.255.255", IpScope.Private)]
    [InlineData("172.32.0.1", IpScope.Public)]
    [InlineData("192.168.1.1", IpScope.Private)]
    [InlineData("127.0.0.1", IpScope.Loopback)]
    [InlineData("169.254.1.1", IpScope.LinkLocal)]
    [InlineData("100.64.0.1", IpScope.SharedAddressSpace)]
    [InlineData("100.128.0.1", IpScope.Public)]
    [InlineData("192.0.2.5", IpScope.Documentation)]
    [InlineData("198.51.100.5", IpScope.Documentation)]
    [InlineData("203.0.113.5", IpScope.Documentation)]
    [InlineData("198.18.0.1", IpScope.Benchmarking)]
    [InlineData("224.0.0.1", IpScope.Multicast)]
    [InlineData("240.0.0.1", IpScope.Reserved)]
    [InlineData("255.255.255.255", IpScope.Broadcast)]
    [InlineData("192.88.99.1", IpScope.ProtocolAssignment)]
    [InlineData("8.8.8.8", IpScope.Public)]
    [InlineData("193.136.128.1", IpScope.Public)]
    public void Classifies_ipv4(string address, IpScope expected) =>
        IpScopes.Classify(System.Net.IPAddress.Parse(address)).ShouldBe(expected);

    [Theory]
    [InlineData("::", IpScope.Unspecified)]
    [InlineData("::1", IpScope.Loopback)]
    [InlineData("fe80::1", IpScope.LinkLocal)]
    [InlineData("fc00::1", IpScope.UniqueLocal)]
    [InlineData("fd12:3456::1", IpScope.UniqueLocal)]
    [InlineData("ff02::1", IpScope.Multicast)]
    [InlineData("2001:db8::1", IpScope.Documentation)]
    [InlineData("3fff::1", IpScope.Documentation)]
    [InlineData("2001:2::1", IpScope.Benchmarking)]
    [InlineData("2002::1", IpScope.ProtocolAssignment)]
    [InlineData("64:ff9b::1", IpScope.ProtocolAssignment)]
    [InlineData("2001::1", IpScope.ProtocolAssignment)]
    [InlineData("2a01:4f8::1", IpScope.Public)]
    [InlineData("2606:4700::1111", IpScope.Public)]
    public void Classifies_ipv6(string address, IpScope expected) =>
        IpScopes.Classify(System.Net.IPAddress.Parse(address)).ShouldBe(expected);

    [Fact]
    public void Judges_ipv4_mapped_addresses_as_ipv4() =>
        IpScopes.Classify(System.Net.IPAddress.Parse("::ffff:10.0.0.1")).ShouldBe(IpScope.Private);

    [Fact]
    public void A_private_address_is_special_purpose_not_merely_unknown()
    {
        var db = DatasetWriter.Open(DatasetWriter.Build(v4: [new(0x02000000, 0x020FFFFF, "FR")]));

        var privateAddress = db.Lookup("10.0.0.5");
        var publicMiss = db.Lookup("9.9.9.9");

        privateAddress.IsSpecialPurpose.ShouldBeTrue();
        privateAddress.Scope.ShouldBe(IpScope.Private);
        publicMiss.IsSpecialPurpose.ShouldBeFalse();
        publicMiss.Scope.ShouldBe(IpScope.Public);
        privateAddress.IsKnown.ShouldBeFalse();
        publicMiss.IsKnown.ShouldBeFalse();
    }
}
