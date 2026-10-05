# eQuantic.IpAtlas

IP geolocation and network intelligence for .NET with **no external services and
no license-encumbered databases**: compile your own dataset from public,
free-to-use sources, load it in memory, and answer *"where is this address, what
kind of network is it, and could its owner really have moved that fast?"* in
nanoseconds.

- **`eQuantic.IpAtlas`** — the runtime: a compact, checksummed binary dataset
  (`.eqatlas`), structure-of-arrays binary-search lookups, country, ASN,
  coordinates, network traits and RFC 6890 scope, plus travel-velocity math for
  impossible-travel risk signals. Zero dependencies, AOT-compatible.
- **`eQuantic.IpAtlas.Compiler`** — the `eqatlas` dotnet tool that fetches the
  sources, builds datasets, verifies them, and measures their accuracy.

📚 **[Full documentation](docs/README.md)**

## Accuracy, and where it comes from

A registry delegation records who was handed a block of addresses and in which
country the paperwork sat. It does not record where the addresses are used, and
for cloud networks the two are not close: AWS, Google and Microsoft register
their estates to a single legal entity and run them on every continent.

`eqatlas accuracy` measures this against the region file each provider publishes
about its own network — 53,909 sampled prefixes across AWS, Google Cloud and
Azure:

| dataset | correct | wrong |
|---|---:|---:|
| registry delegations only | 33.0 % | 67.0 % |
| registries + provider range files | **100.0 %** | 0.0 % |

The second row is not a model that generalises, and the tool says so when it
prints it. **The accuracy comes from ingesting each provider's own file.** Held
out — scoring a dataset built from AWS and Google files against Azure — the
result is exactly the registry-only baseline. Feed a provider's ranges in and
that provider's addresses are right; leave them out and they are wrong roughly
two times in three.

So the honest summary is: this is as accurate as the sources you give it, and
the sources that matter are free. `eqatlas fetch` collects all of them.

→ [Accuracy: method, holdout, and how to reproduce it](docs/accuracy.md)

## Quick start

```bash
dotnet tool install -g eQuantic.IpAtlas.Compiler

eqatlas fetch --into sources
eqatlas build \
  --rir sources/delegated-* \
  --asn sources/ip2asn.tsv \
  --cloud sources/*-ranges.json \
  --anycast sources/cloudflare-v4.txt sources/cloudflare-v6.txt \
  --anonymizer sources/tor-exits.txt \
  --out world.eqatlas

eqatlas verify --dataset world.eqatlas --max-age-days 14
```

That is about 17 MB and a couple of seconds. Sources rank against each other
rather than piling up — cloud provider ranges beat operator geofeeds beat
registry delegations — and each field of each range comes from the highest
ranked source that states it.

For city-level data outside the clouds, harvest the geofeeds operators publish
about their own networks:

```bash
eqatlas fetch --into sources --with-whois
eqatlas geofeeds --whois sources/*.db*.gz --out geofeeds.csv --same-org
```

ARIN and LACNIC publish no bulk database, so their operators' geofeeds are
found a block at a time over RDAP instead:

```bash
eqatlas rdap --delegated sources/delegated-arin sources/delegated-lacnic --out refs.csv
eqatlas geofeeds --references refs.csv --out geofeeds.csv --same-org
```

Both are real crawls of several thousand hosts, and both resume if interrupted.
If you would rather not do them, a dataset built the same way is published
monthly with a checksum and a manifest of the run that produced it:

```bash
curl -fsSLO https://github.com/eQuantic/core-ipatlas/releases/download/dataset/world.eqatlas
eqatlas verify --dataset world.eqatlas --max-age-days 45
```

→ [CLI reference](docs/cli.md) · [Prebuilt datasets](docs/prebuilt-datasets.md) · [Build pipeline](docs/pipeline.md) · [Geofeeds](docs/geofeeds.md) · [Data sources](docs/sources.md)

## Look things up

```csharp
using eQuantic.IpAtlas;

var db = IpAtlasDatabase.Open("world.eqatlas");

var info = db.Lookup("18.184.0.1");
// CountryCode = "DE", Asn = 16509, Traits = Hosting,
// Location = { City = "Frankfurt", Region = "eu-central-1", 50.11, 8.68 }

info.IsHosting          // a datacenter, not a household
info.IsAnycast          // announced from many places, so nowhere in particular
info.IsAnonymizer       // a VPN, proxy, relay or Tor exit
info.Scope              // Public, Private, Loopback, SharedAddressSpace, ...
info.IsLocatablePerson  // public, located, and not a network's own address
```

`Scope` is why `10.0.0.5` and an address simply missing from the dataset are
distinguishable. Special-purpose ranges are classified from the address itself
against the IANA registries (RFC 6890), before any lookup happens, so internal
traffic never reads as "unknown location".

The database is immutable and thread-safe, and a hit allocates nothing. Refresh
by loading a new instance and swapping the reference; `BuiltAt`, `Age` and
`Source` travel in the file header so a stale dataset is visible, not silent.

→ [.NET API guide](docs/api.md) · [Operations](docs/operations.md)

## Impossible travel

```csharp
var verdict = Velocity.Assess(db.Lookup(lastIp), db.Lookup(thisIp), elapsed);

if (verdict.Plausible == false) { /* flag it */ }
```

`Plausible` is three-valued on purpose, and `null` is the interesting one. The
library declines to answer rather than guess when either address is anycast or
an anonymizer, when the events arrived out of order, when both sightings are in
one country too wide for a centroid to say anything, or when nothing located
either side. `Reason` says which, and `Precision` says whether the answer came
from real coordinates or a country centroid.

→ [Impossible travel](docs/impossible-travel.md)

## What it costs

Two real datasets, both built from live sources. The second adds harvested
geofeeds, which is most of the size and all of the city coverage:

| | registries + clouds | + geofeeds | + `--same-org` |
|---|---:|---:|---:|
| IPv4 / IPv6 ranges | 496,288 / 170,028 | 763,571 / 407,195 | 818,625 / 545,552 |
| distinct places | 154 | 27,506 | 30,152 |
| dataset on disk | 17 MB | 34 MB | 41 MB |
| load, including checksum | 30 ms | 44 ms | 50 ms |
| resident after load | 49 MB | 100 MB | 107 MB |
| lookup, random addresses | 174 ns | 171 ns | 170 ns |
| allocation per lookup | 0 bytes | 0 bytes | 0 bytes |
| random addresses answered with a city | — | 5.9 % | 6.2 % |
| build from source files | 2.3 s | 2.7 s | 4.8 s |

Building peaks at 1.1 GB resident for the middle one and 3.3 GB for the last —
a batch job, not something a service does, but worth knowing before you put it
on a small build agent.

## Honesty notes

- **Accuracy is bounded by your sources.** Address space no published geofeed or
  provider file covers falls back to its registry delegation, and for
  residential and business networks that is usually right. For hosting networks
  it usually is not, which is what the traits are for.
- **Anycast has no location.** A single address announced from thirty cities is
  in all of them. The dataset records the flag rather than pretending otherwise,
  and travel assessment refuses to judge it.
- **Coordinates are metropolitan.** Providers name the metro their region runs
  in, not the building, and geofeeds name a city. Precision beyond that is not
  in any free source and is not invented here.
- **City coverage is where operators bothered, and they are not spread evenly.**
  93.6 % of hosting and datacenter space carries a city; 2.8 % of everything
  else does. The average of the two is not a useful number. Operators who run
  infrastructure publish geofeeds and residential ISPs mostly do not, so
  deciding *which datacenter* is close to solved and placing a residential
  subscriber in a city is not something any free first-party source can do.
- **Anonymizer coverage is Tor and whatever you supply.** Commercial VPN and
  proxy space is not covered by any free source at scale, so `IsAnonymizer`
  being false means "not on a list we have", not "not a VPN".
- **`EU` and `AP`** appear in registry data for allocations spanning a region.
  They are deliberately not locatable, because giving them a point would invent
  precision that does not exist.
- **ASN name heuristics are off by default.** A name match is not evidence.

## The `.eqatlas` format

A header, a section table, the sections, and a trailing CRC-32. Readers skip
section kinds they do not recognise, so later datasets can carry new signals
without breaking already-deployed readers. Everything a file claims about itself
is checked before it is believed, and version 1 files still load.

→ [Format specification](docs/format.md)

## Where the data comes from

Every source is published by the organisation it describes and is free to use.

| source | published by | what it gives |
|---|---|---|
| `delegated-*-extended-latest` | AFRINIC, APNIC, ARIN, LACNIC, RIPE NCC | delegation country |
| `ip2asn-combined.tsv` | [iptoasn.com](https://iptoasn.com), from RouteViews | AS numbers |
| `ip-ranges.json` | Amazon Web Services | region per prefix |
| `cloud.json` | Google Cloud | region per prefix |
| ServiceTags | Microsoft Azure | region per prefix |
| `ips-v4` / `ips-v6` | Cloudflare | anycast prefixes |
| `torbulkexitlist` | The Tor Project | exit nodes |
| `*.db.*.gz` | RIPE NCC, APNIC, AFRINIC | geofeed pointers (RFC 9092) |
| geofeeds | network operators, per RFC 8805 | country, region, city |

→ [Data sources: terms, cadence, and what is missing](docs/sources.md)

## Upgrading from 1.x

Recompile: 2.0 is source compatible for most code but not binary compatible.
Datasets written by 1.x still load; datasets written by 2.x need 2.x to read.

The change to check for is `TravelAssessment.Plausible`, which now answers
`null` where 1.x answered `true` or `false` — for out-of-order events, for wide
countries, and for anycast and anonymizer addresses. That is the point of the
change rather than a side effect, and `if (verdict.Plausible != true)` will now
fire on "cannot tell". Check `== false`.

→ [Full upgrade guide](docs/upgrading.md), with every removed member and why

## Contributing

→ [Building, testing, and the public API surface file](docs/contributing.md)
