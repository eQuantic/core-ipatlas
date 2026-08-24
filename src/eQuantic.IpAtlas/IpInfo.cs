namespace eQuantic.IpAtlas;

/// <summary>What the dataset knows about one address.</summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2, upper case, or null when the range carries no country.</param>
/// <param name="Asn">Autonomous system number, or null when the dataset was built without ASN data.</param>
public readonly record struct IpInfo(string? CountryCode, uint? Asn)
{
    /// <summary>An address the dataset holds no range for.</summary>
    public static readonly IpInfo Unknown = new(null, null);

    /// <summary>Whether the dataset had anything at all for the address.</summary>
    public bool IsKnown => CountryCode is not null || Asn is not null;
}
