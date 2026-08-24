# .NET API

[← docs index](README.md)

```bash
dotnet add package eQuantic.IpAtlas
```

Targets `net8.0` and `net10.0`. Zero dependencies, AOT-compatible, and the CI
publishes a real consumer natively with trim and AOT warnings as errors.

## Opening a dataset

```csharp
using eQuantic.IpAtlas;

var db = IpAtlasDatabase.Open("world.eqatlas");
```

The instance is immutable and safe to read from any thread. Loading verifies
the checksum and every internal claim the file makes about itself; see
[the format](format.md) for what is checked and what each failure means.

For a service whose fallback is to keep serving what it has:

```csharp
if (IpAtlasDatabase.TryOpen(path, out var fresh, out var error))
{
    Interlocked.Exchange(ref _database, fresh!);
}
else
{
    _logger.LogWarning("Keeping the current dataset: {Error}", error);
}
```

## Looking an address up

```csharp
var info = db.Lookup("18.184.0.1");
// or db.Lookup(IPAddress) to skip parsing
```

`IpInfo` carries five things:

```csharp
public readonly record struct IpInfo(
    string? CountryCode,     // ISO 3166-1 alpha-2, or null
    uint? Asn,               // autonomous system, or null
    NetworkTraits Traits,    // what kind of network
    IpScope Scope,           // whether it is publicly routable at all
    IpLocation? Location);   // coordinates and place names, when known
```

A successful lookup allocates nothing: country codes come from an interned
table and `IpLocation` is a struct.

### Scope: "not our data" versus "never anyone's data"

```csharp
db.Lookup("10.0.0.5").Scope        // Private
db.Lookup("127.0.0.1").Scope       // Loopback
db.Lookup("100.64.0.1").Scope      // SharedAddressSpace  (carrier-grade NAT)
db.Lookup("224.0.0.1").Scope       // Multicast
db.Lookup("8.8.8.8").Scope         // Public
```

Special-purpose addresses are classified from the address itself against the
IANA registries (RFC 6890), before any dataset lookup happens. This is the
difference between "we have no data for this address" and "this address was
never going to be in any dataset" — a distinction you cannot make from a null
country code, and one that decides whether an unknown answer is a gap or
internal traffic being scored as suspicious.

```csharp
if (info.IsSpecialPurpose) { /* internal, not a signal */ }
```

Full list: `Public`, `Unspecified`, `Loopback`, `Private`, `LinkLocal`,
`SharedAddressSpace`, `UniqueLocal`, `Documentation`, `Benchmarking`,
`Multicast`, `Broadcast`, `ProtocolAssignment`, `Reserved`.

> Note for tests and demos: RFC 5737 documentation ranges such as
> `203.0.113.0/24` are classified before the lookup and will never return
> dataset content. Use ordinary public space in fixtures.

### Traits: what kind of network it is

```csharp
info.IsHosting       // a datacenter, cloud or hosting network
info.IsAnycast       // announced from many places, so nowhere in particular
info.IsAnonymizer    // a known VPN, proxy, relay or Tor exit
info.Traits          // the flags themselves: also Mobile, Satellite
```

These are what a risk decision usually turns on. An address in a datacenter is
not a person sitting somewhere, and `IsHosting` on an otherwise unlocated range
is a more honest signal than its country.

### Location and provenance

```csharp
if (info.Location is { } place)
{
    place.City;            // "Frankfurt", or null
    place.Region;          // "eu-central-1" or "PT-11", or null
    place.Latitude;        // double?, null when the source carried none
    place.HasCoordinates;
    place.Source;          // CloudProvider | Geofeed | RegistryDelegation | Override
}
```

`Source` matters. "Germany, because Amazon publishes that this prefix runs in
eu-central-1" and "Germany, because a registry recorded the delegation in 2012"
are different facts, and only one of them survives the address being reassigned.

### The composite you usually want

```csharp
info.IsLocatablePerson
```

True only when the address is publicly routable, has a country, and is neither
anycast nor an anonymizer. That is the condition under which a location can be
read as *a person's* location rather than a network's.

## Impossible travel

```csharp
using eQuantic.IpAtlas.Geo;

var verdict = Velocity.Assess(db.Lookup(lastIp), db.Lookup(thisIp), elapsed);

if (verdict.Plausible == false) { /* flag it */ }
```

`Plausible` is `bool?`, and `null` is the interesting answer. See
[Impossible travel](impossible-travel.md) for every case where it declines and
why. In short: check `== false`, never `!= true`.

## Refreshing without downtime

```csharp
private volatile IpAtlasDatabase _database;

// on a timer, or on a file-change notification
if (IpAtlasDatabase.TryOpen(path, out var fresh, out _))
{
    _database = fresh!;   // readers in flight finish against the old instance
}
```

The old instance stays valid for anything already using it and is collected
when nothing is. `BuiltAt`, `Age` and `Source` travel in the header, so a stale
dataset is visible rather than silent:

```csharp
if (db.Age > TimeSpan.FromDays(14))
{
    _logger.LogWarning("Dataset built {Age:F0} days ago from {Source}", db.Age.TotalDays, db.Source);
}
```

See [Operations](operations.md) for the rest.

## Writing a dataset

`AtlasWriter` is in the runtime package, so building a small dataset — a test
fixture, a fallback, a hand-curated overlay — needs no tool and no reimplementing
of the format:

```csharp
var writer = new AtlasWriter("test fixture", DateTimeOffset.UnixEpoch);

writer.AddPrefix("45.10.0.0/24", new AtlasRecord("PT", 1930));
writer.AddPrefix("45.20.0.0/24", new AtlasRecord(
    "DE", 16509, NetworkTraits.Hosting, LocationSource.CloudProvider,
    Latitude: 50.11, Longitude: 8.68, Region: "eu-central-1", City: "Frankfurt"));

using var file = File.Create("fixture.eqatlas");
writer.WriteTo(file);
```

It sorts ranges for you, interns places so a shared one is written once, and
**refuses to produce a file the reader would reject** — overlapping or inverted
ranges throw at `WriteTo` rather than surfacing as a load failure later, far
from the cause.

This is the same writer the compiler uses, so a fixture is built by the same
code as a world dataset. That is deliberate: a format whose writer and reader
are maintained apart is a format that drifts.

`AddPrefix` also accepts a bare address as a single host, and there are
`AddV4`/`AddV6` overloads taking integers and an `Add` taking two `IPAddress`
values.

## Building datasets in-process

The compiler is also a library, if you would rather not shell out:

```bash
dotnet add package eQuantic.IpAtlas.Compiler
```

```csharp
using eQuantic.IpAtlas.Compiler;

var builder = new DatasetBuilder()
    .AddRegistry(RirDelegatedParser.Parse(File.OpenText("delegated-ripencc")))
    .AddAsns(AsnTsvParser.Parse(File.OpenText("ip2asn.tsv")))
    .AddCloud(CloudRangesParser.Parse(File.OpenRead("aws-ranges.json")))
    .AddGeofeed(GeofeedParser.Parse(File.OpenText("geofeeds.csv")))
    .AddOverrides([AtlasEntry.FromPrefix("203.0.113.0/24", "PT", city: "Lisboa")!.Value]);

using var output = File.Create("world.eqatlas");
var report = builder.Write(output, source: "my build", builtAt: DateTimeOffset.UtcNow);
```

Layers are ranked, not merged blindly — see [the pipeline](pipeline.md). Note
that writing directly to the destination gives up the atomic replace the CLI
does; write to a temporary file and rename if the target is live.
