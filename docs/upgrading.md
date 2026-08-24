# Upgrading from 1.x to 2.0

[← docs index](README.md)

## Short version

- **Recompile.** Version 2.0 is not binary compatible. Most code compiles
  unchanged, but an assembly built against 1.x will fail at runtime.
- **Your datasets still load.** Files written by 1.x are read by 2.x.
- **Rebuild them anyway.** Everything 2.0 is for — correct countries for cloud
  address space, traits, scopes, coordinates — lives in data a 1.x dataset does
  not carry.
- **Check where you read `Plausible`.** It now answers `null` in cases 1.x
  answered `true` or `false`, on purpose.

## Datasets

| | |
|---|---|
| written by 1.x, read by 2.x | works; reports `FormatVersion == 1` |
| written by 2.x, read by 1.x | refused, with a message naming the versions |

A version 1 dataset loads and answers country and ASN exactly as before. It
carries no traits, no scope-independent location, and no coordinates, because
the format had nowhere to put them. Rebuild to get them:

```bash
eqatlas fetch --into sources
eqatlas build --rir sources/delegated-* --asn sources/ip2asn.tsv \
  --cloud sources/*-ranges.json \
  --anycast sources/cloudflare-v4.txt sources/cloudflare-v6.txt \
  --anonymizer sources/tor-exits.txt \
  --out world.eqatlas
```

See [Accuracy](accuracy.md) for what that rebuild is worth: on cloud address
space it is the difference between 33 % and 100 % correct.

## Binary breaks

These are the members that existed in 1.0.0 and do not exist in 2.0.0, as
reported by package validation against the published 1.0.0 package:

| removed | replaced by |
|---|---|
| `IpInfo(string?, uint?)` | `IpInfo(string?, uint?, NetworkTraits, IpScope, IpLocation?)` |
| `IpInfo.Deconstruct(out string?, out uint?)` | five-part deconstruction |
| `TravelAssessment(bool?, double?, double?)` | `TravelAssessment(bool?, double?, double?, TravelPrecision, TravelReason)` |
| `TravelAssessment.Deconstruct(out bool?, out double?, out double?)` | five-part deconstruction |
| `AtlasFormat.WriteHeader(Stream, DateTimeOffset, string, int, int)` | `WriteHeader(Stream, DateTimeOffset, string, IReadOnlyList<AtlasSection>)` |
| `AtlasFormat.WriteV4Record(Stream, uint, uint, ushort, uint)` | `WriteV4Record(Span<byte>, uint, uint, ushort, uint, ushort, uint)` |
| `AtlasFormat.WriteV6Record(Stream, UInt128, UInt128, ushort, uint)` | `WriteV6Record(Span<byte>, UInt128, UInt128, ushort, uint, ushort, uint)` |

The two record constructors are the ones that will surprise people, because
**they are source compatible and not binary compatible**. The new parameters
have defaults, so `new IpInfo("PT", 1930)` still compiles — but the two-argument
constructor no longer exists as a symbol, so an assembly compiled against 1.x
will not find it. Recompiling is enough; no edit is needed.

Positional deconstruction does need an edit:

```csharp
var (country, asn) = db.Lookup(ip);                          // 1.x
var (country, asn, traits, scope, location) = db.Lookup(ip); // 2.x
var info = db.Lookup(ip);                                    // or just use the properties
```

## Renames

| 1.x | 2.x | why |
|---|---|---|
| `IpFlags` | `NetworkTraits` | a `[Flags]` enum should not be named `…Flags` (CA1711), and "traits" is what it describes |
| `IpInfo.Flags` | `IpInfo.Traits` | follows the type |
| `AtlasFormat.PackFlags` / `UnpackFlags` | `PackTraits` / `UnpackTraits` | follows the type |

## Behaviour changes worth reading twice

### `Plausible` answers `null` where 1.x answered

This is the point of the change rather than a side effect. 1.x gave a verdict in
cases where the data could not support one:

| case | 1.x | 2.x |
|---|---|---|
| events out of order (negative interval) | `false` — an impossible-travel alert | `null`, `Reason = OutOfOrder` |
| same country, any size | `true` | `true` for small countries; `null` with `CountryTooLarge` for wide ones |
| anycast or anonymizer address | judged as if it were a person's location | `null`, `Reason = NotAPersonsLocation` |

If you wrote `if (!verdict.Plausible ?? false)` or `if (verdict.Plausible != true)`,
those now fire on "cannot tell". **Check `== false`.**

```csharp
if (verdict.Plausible == false) { /* the geometry rules it out */ }
```

See [Impossible travel](impossible-travel.md).

### Special-purpose addresses are no longer "unknown"

In 1.x, `10.0.0.5` and an address simply missing from the dataset both answered
with nulls. In 2.x the first has `Scope == IpScope.Private`. If you were
treating unknown addresses as suspicious, internal traffic stops being caught by
that.

```csharp
if (info.IsSpecialPurpose) { /* internal, not a signal */ }
```

### Country codes are validated

1.x packed any two characters, so a malformed source could produce a country
code of `É1` or `©X`. 2.x packs only two letters A–Z; anything else becomes no
country at all.

### Territories gained centroids

49 codes the registries actually emit — `RE`, `GF`, `GP`, `MQ`, `GL`, `VG`,
`KY`, `IM`, `GG`, `JE`, `BM`, `CW`, `XK` and others — used to answer "unknown"
in travel assessment and now answer with a distance. `EU` and `AP` deliberately
still do not, because they are regions rather than places.

## Compiler API

If you build datasets in-process rather than through the CLI, the shape changed:

```csharp
// 1.x
builder.AddCountries(RirDelegatedParser.Parse(reader));   // IEnumerable<CountryRange>
builder.AddAsns(AsnTsvParser.Parse(reader));              // IEnumerable<AsnRange>

// 2.x — one entry type, ranked layers
builder.AddRegistry(RirDelegatedParser.Parse(reader));    // IEnumerable<AtlasEntry>
builder.AddAsns(AsnTsvParser.Parse(reader));
builder.AddGeofeed(...);
builder.AddCloud(...);
builder.AddOverrides(...);
```

`CountryRange` and `AsnRange` are gone; both parsers emit `AtlasEntry`.
`DatasetBuilder.Write` now returns a `BuildReport` instead of `void`. See
[the pipeline](pipeline.md) for what the ranks mean.

## Nothing to do

- `IpAtlasDatabase.Open`, `Lookup(string)` and `Lookup(IPAddress)` are unchanged.
- `IpInfo.CountryCode`, `Asn`, `IsKnown` are unchanged.
- `Velocity.Assess(string?, string?, TimeSpan)` still exists and still means the
  same thing.
- `Velocity.HaversineKm` and `CountryCentroids.Get` are unchanged.
