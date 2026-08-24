using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// One range and everything a single source claimed about it. Every parser
/// produces these, so the builder merges registry delegations, geofeeds, cloud
/// ranges and ASN data through one code path instead of one path per source.
/// </summary>
/// <param name="IsV6">Whether the range is IPv6.</param>
/// <param name="Start">First address, inclusive.</param>
/// <param name="End">Last address, inclusive.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, when the source carried one.</param>
/// <param name="Asn">Autonomous system number, or zero for none.</param>
/// <param name="Traits">What kind of network the source says this is.</param>
/// <param name="Latitude">Degrees north, when the source carried coordinates.</param>
/// <param name="Longitude">Degrees east, when the source carried coordinates.</param>
/// <param name="Region">Subdivision name or code, when the source carried one.</param>
/// <param name="City">City name, when the source carried one.</param>
public readonly record struct AtlasEntry(
    bool IsV6,
    UInt128 Start,
    UInt128 End,
    string? CountryCode = null,
    uint Asn = 0,
    NetworkTraits Traits = NetworkTraits.None,
    double? Latitude = null,
    double? Longitude = null,
    string? Region = null,
    string? City = null)
{
    /// <summary>Whether the entry carries anything worth recording.</summary>
    public bool IsEmpty =>
        CountryCode is null && Asn == 0 && Traits == NetworkTraits.None
        && Latitude is null && Region is null && City is null;

    /// <summary>Whether the entry names a place, beyond just a country.</summary>
    public bool HasPlace => Latitude is not null || Region is not null || City is not null;

    /// <summary>
    /// Builds an entry from a CIDR prefix, or from a bare address, which is read
    /// as a single host. Returns null when it does not parse: source files are
    /// other people's output and a line that makes no sense is data to skip, not
    /// an exception to throw.
    /// </summary>
    public static AtlasEntry? FromPrefix(
        string prefix, string? countryCode = null, uint asn = 0, NetworkTraits traits = NetworkTraits.None,
        double? latitude = null, double? longitude = null, string? region = null, string? city = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var slash = prefix.AsSpan().IndexOf('/');
        IPAddress? address;
        int bits;
        if (slash < 0)
        {
            if (!IPAddress.TryParse(prefix, out address))
            {
                return null;
            }

            bits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        }
        else if (!IPAddress.TryParse(prefix.AsSpan(0, slash), out address)
            || !int.TryParse(prefix.AsSpan(slash + 1), out bits))
        {
            return null;
        }

        var isV6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var width = isV6 ? 128 : 32;
        if (bits < 0 || bits > width)
        {
            return null;
        }

        var hostBits = width - bits;
        var size = hostBits >= 128 ? UInt128.MaxValue : (UInt128.One << hostBits) - UInt128.One;
        var start = ToNumber(address, isV6) & ~size;
        return new AtlasEntry(isV6, start, start | size, countryCode, asn, traits, latitude, longitude, region, city);
    }

    /// <summary>
    /// The range written as a CIDR, which is the notation every source and
    /// RFC 8805 itself uses. Entries built from a prefix are aligned and their
    /// size is a power of two, so the prefix length is the address width less
    /// the size's logarithm. A size of zero is the whole space having wrapped,
    /// which is a prefix length of nothing.
    /// </summary>
    public string ToCidr()
    {
        var width = IsV6 ? 128 : 32;
        var size = End - Start + UInt128.One;
        var bits = size == UInt128.Zero ? 0 : width - (int)UInt128.Log2(size);

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, Start);
        var address = new IPAddress(IsV6 ? bytes : bytes[12..]);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{address}/{bits}");
    }

    /// <summary>Whether this entry's range lies wholly inside another's.</summary>
    public bool IsInside(AtlasEntry outer) =>
        IsV6 == outer.IsV6 && Start >= outer.Start && End <= outer.End;

    /// <summary>An address as the big-endian integer the dataset sorts on.</summary>
    public static UInt128 ToNumber(IPAddress address, bool isV6)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out var written);
        return isV6
            ? BinaryPrimitives.ReadUInt128BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes[..written]);
    }
}
