namespace eQuantic.IpAtlas;

/// <summary>What the dataset knows about one address.</summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2, upper case, or null when nothing located the range.</param>
/// <param name="Asn">Autonomous system number, or null when the dataset was built without ASN data.</param>
/// <param name="Traits">What kind of network this is: hosting, anycast, anonymizer and so on.</param>
/// <param name="Scope">Whether the address is publicly routable at all.</param>
/// <param name="Location">Coordinates and place names, when a source better than a delegation supplied them.</param>
public readonly record struct IpInfo(
    string? CountryCode,
    uint? Asn,
    NetworkTraits Traits = NetworkTraits.None,
    IpScope Scope = IpScope.Public,
    IpLocation? Location = null)
{
    /// <summary>An address the dataset holds no range for.</summary>
    public static readonly IpInfo Unknown = new(null, null);

    /// <summary>Whether the dataset had anything at all for the address.</summary>
    public bool IsKnown => CountryCode is not null || Asn is not null;

    /// <summary>
    /// Whether the address is special-purpose — private, loopback, multicast and
    /// the rest. These are never in any dataset, and reading their absence as
    /// "unknown location" is how internal traffic ends up scored as suspicious.
    /// </summary>
    public bool IsSpecialPurpose => Scope != IpScope.Public;

    /// <summary>Whether the range belongs to a datacenter, cloud or hosting network.</summary>
    public bool IsHosting => (Traits & NetworkTraits.Hosting) != 0;

    /// <summary>Whether the range is announced from many places, so no single location is right.</summary>
    public bool IsAnycast => (Traits & NetworkTraits.Anycast) != 0;

    /// <summary>Whether the range is a known VPN, proxy, relay or Tor exit.</summary>
    public bool IsAnonymizer => (Traits & NetworkTraits.Anonymizer) != 0;

    /// <summary>
    /// Whether this address can carry a meaningful travel signal. Anycast and
    /// anonymizer addresses have a location, but it is the network's, not the
    /// person's, and treating it as theirs is exactly how impossible-travel
    /// checks generate false positives.
    /// </summary>
    public bool IsLocatablePerson =>
        Scope == IpScope.Public && CountryCode is not null && (Traits & (NetworkTraits.Anycast | NetworkTraits.Anonymizer)) == 0;
}
