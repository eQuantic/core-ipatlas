# eQuantic.IpAtlas

IP geolocation and network intelligence for .NET with **no external services and no license-encumbered
databases**: compile your own dataset from the five RIRs' public delegation
files, load it in memory, and answer *"where is this address, and could its
owner really have moved that fast?"* in nanoseconds.

- **`eQuantic.IpAtlas`** — the runtime: a compact binary dataset (`.eqatlas`),
  structure-of-arrays binary-search lookups (~130 ns), country + ASN answers,
  and travel-velocity math for impossible-travel risk signals. Zero
  dependencies, AOT-compatible.
- **`eQuantic.IpAtlas.Compiler`** — the `eqatlas` dotnet tool and library that
  builds datasets from RIR *delegated-extended* files (country, authoritative
  and license-free) and optional ip-to-ASN TSV data.

## Build a dataset

```bash
# The registries' own public records (pick the regions you serve, or all five)
curl -O https://ftp.ripe.net/pub/stats/ripencc/delegated-ripencc-extended-latest
curl -O https://ftp.arin.net/pub/stats/arin/delegated-arin-extended-latest
curl -O https://ftp.apnic.net/stats/apnic/delegated-apnic-extended-latest
curl -O https://ftp.lacnic.net/pub/stats/lacnic/delegated-lacnic-extended-latest
curl -O https://ftp.afrinic.net/pub/stats/afrinic/delegated-afrinic-extended-latest

dotnet tool install -g eQuantic.IpAtlas.Compiler
eqatlas build --rir delegated-*-extended-latest --out world.eqatlas --source "5 RIRs $(date +%F)"
```

All five files compile to a few megabytes. Add `--asn ip2asn-combined.tsv`
(e.g. from iptoasn.com) when you also want autonomous-system answers.

## Look things up

```csharp
using eQuantic.IpAtlas;
using eQuantic.IpAtlas.Geo;

var db = IpAtlasDatabase.Open("world.eqatlas");

var info = db.Lookup("193.136.128.1");   // IpInfo { CountryCode = "PT", Asn = ... }

// Impossible travel: could the same person sign in from both places?
var verdict = Velocity.Assess(
    fromCountry: "PT", toCountry: "JP", elapsed: TimeSpan.FromMinutes(10));
// verdict.Plausible == false — ~65,000 km/h is not a commute
```

`TravelAssessment.Plausible` is three-valued on purpose: `null` means a side
was unknown, and "we cannot tell" never masquerades as an answer.

The database is immutable and thread-safe; refresh by loading a new instance
and swapping the reference. `BuiltAt` and `Source` travel in the file header
so a stale dataset is visible, not silent.

## Honesty notes

- RIR data maps *delegation* country — where the registry recorded the
  holder — which is the right granularity for risk signals, not for maps.
- Country distance uses embedded centroids (±1–2°); precision is deliberate
  for airliner-speed questions. City-level data can be layered in later
  without changing the format.
- Overlapping source ranges resolve first-wins; ranges with neither country
  nor ASN are dropped at build time.
