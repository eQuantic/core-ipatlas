namespace eQuantic.IpAtlas;

/// <summary>
/// What kind of network a range is, beyond where it is. These are the flags a
/// risk decision actually turns on: an address in a datacenter is not a person
/// sitting somewhere, and an anycast address is not anywhere in particular.
/// </summary>
[Flags]
public enum NetworkTraits : byte
{
    /// <summary>Nothing known beyond location.</summary>
    None = 0,

    /// <summary>A datacenter, cloud or hosting network — traffic from here is infrastructure, not a household.</summary>
    Hosting = 1 << 0,

    /// <summary>Announced from many places at once, so no single location is correct.</summary>
    Anycast = 1 << 1,

    /// <summary>A mobile carrier network.</summary>
    Mobile = 1 << 2,

    /// <summary>Satellite access, where the ground location and the registration can differ wildly.</summary>
    Satellite = 1 << 3,

    /// <summary>A known VPN, proxy, relay or Tor exit — the location is the service's, not the user's.</summary>
    Anonymizer = 1 << 4,
}

/// <summary>
/// Where a range's location came from. A country the holder published for its
/// own network is worth more than a country a registry recorded when the block
/// was handed out, and callers deserve to know which one they got.
/// </summary>
public enum LocationSource : byte
{
    /// <summary>No location.</summary>
    None = 0,

    /// <summary>The country a regional registry recorded for the delegation.</summary>
    RegistryDelegation = 1,

    /// <summary>A self-published geofeed (RFC 8805) from the network's own operator.</summary>
    Geofeed = 2,

    /// <summary>A cloud provider's own published ranges, which name the region they run in.</summary>
    CloudProvider = 3,

    /// <summary>A local override supplied at build time.</summary>
    Override = 4,
}
