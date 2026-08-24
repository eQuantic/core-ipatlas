using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace eQuantic.IpAtlas;

/// <summary>
/// What an address is for, per the IANA special-purpose registries (RFC 6890
/// and its updates). This is the difference between "we have no data for this
/// address" and "this address was never going to be in any dataset" — a
/// distinction a caller cannot make from a null country code, and one that
/// decides whether an unknown answer is a gap or a misrouted request.
/// </summary>
public enum IpScope : byte
{
    /// <summary>Globally routable space: the only scope a dataset can meaningfully locate.</summary>
    Public = 0,

    /// <summary>0.0.0.0 or :: — "this host, this network".</summary>
    Unspecified = 1,

    /// <summary>127.0.0.0/8 or ::1 — the host itself.</summary>
    Loopback = 2,

    /// <summary>RFC 1918 space: 10/8, 172.16/12, 192.168/16.</summary>
    Private = 3,

    /// <summary>169.254.0.0/16 or fe80::/10 — link-local autoconfiguration.</summary>
    LinkLocal = 4,

    /// <summary>100.64.0.0/10 — carrier-grade NAT, shared between subscribers.</summary>
    SharedAddressSpace = 5,

    /// <summary>fc00::/7 — IPv6 unique local addresses.</summary>
    UniqueLocal = 6,

    /// <summary>Ranges reserved for documentation and examples.</summary>
    Documentation = 7,

    /// <summary>198.18.0.0/15 and 2001:2::/48 — network benchmarking.</summary>
    Benchmarking = 8,

    /// <summary>224.0.0.0/4 or ff00::/8.</summary>
    Multicast = 9,

    /// <summary>255.255.255.255 — limited broadcast.</summary>
    Broadcast = 10,

    /// <summary>IETF protocol assignments, including Teredo, 6to4 and NAT64 prefixes.</summary>
    ProtocolAssignment = 11,

    /// <summary>240.0.0.0/4 and other space reserved for future use.</summary>
    Reserved = 12,
}

/// <summary>Classifies addresses against the IANA special-purpose registries.</summary>
public static class IpScopes
{
    /// <summary>Classifies an address; IPv4-mapped IPv6 addresses are judged as IPv4.</summary>
    public static IpScope Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            return address.TryWriteBytes(bytes, out _)
                ? ClassifyV4(BinaryPrimitives.ReadUInt32BigEndian(bytes))
                : IpScope.Reserved;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            return address.TryWriteBytes(bytes, out _)
                ? ClassifyV6(BinaryPrimitives.ReadUInt128BigEndian(bytes))
                : IpScope.Reserved;
        }

        return IpScope.Reserved;
    }

    /// <summary>Classifies an IPv4 address held as a big-endian integer.</summary>
    public static IpScope ClassifyV4(uint address) => address switch
    {
        0xFFFFFFFF => IpScope.Broadcast,
        0x00000000 => IpScope.Unspecified,
        _ when Matches(address, 0x00000000, 8) => IpScope.Reserved,
        _ when Matches(address, 0x0A000000, 8) => IpScope.Private,
        _ when Matches(address, 0x64400000, 10) => IpScope.SharedAddressSpace,
        _ when Matches(address, 0x7F000000, 8) => IpScope.Loopback,
        _ when Matches(address, 0xA9FE0000, 16) => IpScope.LinkLocal,
        _ when Matches(address, 0xAC100000, 12) => IpScope.Private,
        _ when Matches(address, 0xC0000200, 24) => IpScope.Documentation,
        _ when Matches(address, 0xC6336400, 24) => IpScope.Documentation,
        _ when Matches(address, 0xCB007100, 24) => IpScope.Documentation,
        _ when Matches(address, 0xC0000000, 24) => IpScope.ProtocolAssignment,
        _ when Matches(address, 0xC0586300, 24) => IpScope.ProtocolAssignment,
        _ when Matches(address, 0xC01FC400, 24) => IpScope.ProtocolAssignment,
        _ when Matches(address, 0xC0AF3000, 24) => IpScope.ProtocolAssignment,
        _ when Matches(address, 0xC0A80000, 16) => IpScope.Private,
        _ when Matches(address, 0xC6120000, 15) => IpScope.Benchmarking,
        _ when Matches(address, 0xE0000000, 4) => IpScope.Multicast,
        _ when Matches(address, 0xF0000000, 4) => IpScope.Reserved,
        _ => IpScope.Public,
    };

    /// <summary>Classifies an IPv6 address held as a big-endian integer.</summary>
    public static IpScope ClassifyV6(UInt128 address)
    {
        if (address == UInt128.Zero)
        {
            return IpScope.Unspecified;
        }

        if (address == UInt128.One)
        {
            return IpScope.Loopback;
        }

        return address switch
        {
            _ when Matches(address, Prefix(0xFF00, 0), 8) => IpScope.Multicast,
            _ when Matches(address, Prefix(0xFE80, 0), 10) => IpScope.LinkLocal,
            _ when Matches(address, Prefix(0xFC00, 0), 7) => IpScope.UniqueLocal,
            _ when Matches(address, Prefix(0x2001, 0x0DB8UL << 32), 32) => IpScope.Documentation,
            _ when Matches(address, Prefix(0x3FFF, 0), 20) => IpScope.Documentation,
            _ when Matches(address, Prefix(0x2001, 0x0002UL << 32), 48) => IpScope.Benchmarking,
            _ when Matches(address, Prefix(0x2002, 0), 16) => IpScope.ProtocolAssignment,
            _ when Matches(address, Prefix(0x0064, 0xFF9BUL << 32), 96) => IpScope.ProtocolAssignment,
            _ when Matches(address, Prefix(0x0064, (0xFF9BUL << 32) | (0x0001UL << 16)), 48) => IpScope.ProtocolAssignment,
            _ when Matches(address, Prefix(0x0100, 0), 64) => IpScope.Reserved,
            _ when Matches(address, Prefix(0x2001, 0), 23) => IpScope.ProtocolAssignment,
            _ => IpScope.Public,
        };
    }

    private static bool Matches(uint address, uint prefix, int bits) =>
        (address & (bits == 0 ? 0u : uint.MaxValue << (32 - bits))) == prefix;

    private static bool Matches(UInt128 address, UInt128 prefix, int bits) =>
        (address & (UInt128.MaxValue << (128 - bits))) == prefix;

    /// <summary>Builds a UInt128 from the leading 16 bits and the rest of the top 64.</summary>
    private static UInt128 Prefix(ushort leading, ulong remainder) =>
        new(((ulong)leading << 48) | remainder, 0);
}
