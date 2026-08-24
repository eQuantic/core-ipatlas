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
  sources, builds datasets from them, verifies datasets, and measures their
  accuracy.

## Accuracy, and where it comes from

A registry delegation records who was handed a block of addresses and in which
country the paperwork sat. It does not record where the addresses are used, and
for cloud networks the two are not close: AWS, Google and Microsoft register
their estates to a single legal entity and run them on every continent.

`eqatlas accuracy` measures this. Ground truth is the region file each provider
publishes about its own network, sampled one address per prefix:

| dataset | samples | correct | wrong |
|---|---:|---:|---:|
| registry delegations only | 53,909 | 33.0 % | 67.0 % |
| registries + provider range files | 53,909 | **100.0 %** | 0.0 % |

The second row is not a model that generalises, and the tool says so when it
prints it. **The accuracy comes from ingesting each provider's own file.** Held
out — scoring a dataset built from AWS and Google files against Azure — the
result is exactly the registry-only baseline. Feed a provider's ranges in and
that provider's addresses are right; leave them out and they are wrong roughly
two times in three.

So the honest summary is: this is as accurate as the sources you give it, and
the sources that matter are free. `eqatlas fetch` collects all of them.

Reproduce the table yourself:

```bash
eqatlas fetch --into sources
eqatlas build --rir sources/delegated-* --asn sources/ip2asn.tsv --out baseline.eqatlas
eqatlas build --rir sources/delegated-* --asn sources/ip2asn.tsv \
  --cloud sources/*-ranges.json \
  --anycast sources/cloudflare-v4.txt sources/cloudflare-v6.txt \
  --out world.eqatlas
eqatlas accuracy --dataset world.eqatlas --baseline baseline.eqatlas \
  --truth sources/*-ranges.json
```

CI reruns this weekly against the live sources, so a publisher changing their
format surfaces there rather than in your fraud dashboard.

## Build a dataset

```bash
dotnet tool install -g eQuantic.IpAtlas.Compiler
eqatlas fetch --into sources
```

That pulls the five registries' delegation files, ip-to-ASN data, the AWS,
Google Cloud and Azure range files, Cloudflare's anycast prefixes and the Tor
Project's exit list. Azure publishes behind a page rather than at a fixed URL,
so it is fetched best-effort: if discovery fails the fetch carries on and prints
the manual step.

Sources rank against each other rather than piling up. From lowest to highest:
registry delegations, ASN data, operator geofeeds (RFC 8805), cloud provider
ranges, and your own overrides. Each field of each range comes from the highest
ranked source that states it, and network traits accumulate across all of them.

```bash
eqatlas build \
  --rir sources/delegated-* \
  --asn sources/ip2asn.tsv \
  --cloud sources/*-ranges.json \
  --anycast sources/cloudflare-v4.txt sources/cloudflare-v6.txt \
  --anonymizer sources/tor-exits.txt \
  --geofeed geofeeds.csv \
  --out world.eqatlas
```

A full world dataset is about 17 MB and takes a couple of seconds to build. The
build writes to a temporary file and renames it into place, so rebuilding over
the dataset a running service is serving cannot leave it truncated.

```bash
eqatlas verify --dataset world.eqatlas --max-age-days 14
eqatlas lookup --dataset world.eqatlas --ip 18.184.0.1
```

### Geofeeds: city-level data outside the clouds

Operators publish where their own addresses are, as an RFC 8805 file pointed at
from their registry objects. Across the RIPE, APNIC and AFRINIC database dumps
there are 91,202 such pointers to 5,314 distinct feeds. `eqatlas geofeeds`
harvests them:

```bash
eqatlas fetch --into sources --with-whois     # adds the registry database dumps
eqatlas geofeeds --whois sources/*.db*.gz --out geofeeds.csv
```

It is a real crawl — thousands of small files on thousands of hosts — so it is a
separate command rather than part of `fetch`, with `--concurrency`, `--timeout`,
`--attempts` and `--limit` to bound it. Parsing the dumps alone takes about eight
seconds and 84 MB, streaming.

**Every prefix is checked against the registry objects that pointed at its
feed.** A geofeed is a CSV on a web server, and the web server does not know
which addresses its owner holds; without that check, publishing a file would be
enough to relocate anyone's addresses. In a 200-feed sample, one feed alone
claimed 86,151 prefixes it had no registry object for. Those were discarded, and
the command names the feeds that overclaim rather than folding them into a
total.

ARIN and LACNIC do not publish bulk whois under terms this can use, so their
geofeeds are not reachable this way. That is a real gap, not an oversight.

## Look things up

```csharp
using eQuantic.IpAtlas;
using eQuantic.IpAtlas.Geo;

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

The database is immutable and thread-safe. A hit allocates nothing. Refresh by
loading a new instance and swapping the reference; `BuiltAt`, `Age` and `Source`
travel in the file header so a stale dataset is visible, not silent.

## Impossible travel

```csharp
var verdict = Velocity.Assess(db.Lookup(lastIp), db.Lookup(thisIp), elapsed);

if (verdict.Plausible == false) { /* flag it */ }
```

`Plausible` is three-valued on purpose, and `null` is the interesting one. The
library declines to answer rather than guess when:

- either address is anycast or an anonymizer, because the location belongs to
  the network and not to the person (`NotAPersonsLocation`)
- the events arrived out of order, which is clock skew, not teleportation
  (`OutOfOrder`)
- both sightings are in one country wide enough that a centroid says nothing —
  ten minutes apart in Russia could be one office or six thousand kilometres
  (`CountryTooLarge`)
- nothing located either side (`NotLocated`)

`Precision` tells you which kind of answer you got. Where a geofeed or a cloud
provider supplied coordinates it is `Coordinates`, and two cities inside one
country become distinguishable; otherwise it is `Country` and the granularity is
continental.

## What it costs

Measured on a full world dataset of 496,288 IPv4 and 170,028 IPv6 ranges:

| | |
|---|---|
| dataset on disk | 17 MB |
| load, including checksum verification | 30 ms |
| resident after load | 49 MB |
| lookup, random addresses | 174 ns |
| allocation per lookup | 0 bytes |
| build from source files | 2.3 s |

`benchmarks/` holds the BenchmarkDotNet project behind these; on a synthetic
500,000-range dataset a lookup is ~89 ns. Real-world lookups cost more because
random addresses across a larger dataset miss cache, which is the number worth
quoting.

## Honesty notes

- **Accuracy is bounded by your sources.** Address space no published geofeed or
  provider file covers falls back to its registry delegation, and for
  residential and business networks that is usually right. For hosting networks
  it usually is not, which is what the traits are for: `IsHosting` on an
  otherwise unlocated range is a more honest signal than its country.
- **Anycast has no location.** A single address announced from thirty cities is
  in all of them. The dataset records the flag rather than pretending otherwise,
  and travel assessment refuses to judge it.
- **Coordinates are metropolitan.** Providers name the metro their region runs
  in, not the building, and geofeeds name a city. Precision beyond that is not
  in any free source and is not invented here.
- **Country centroids are ±1–2°** and only ever answer continental questions.
  `EU` and `AP` appear in registry data for allocations spanning a region; they
  are deliberately not locatable, because giving them a point would invent
  precision that does not exist.
- **ASN name heuristics are off by default.** `--asn-heuristics` will guess
  hosting and mobile from AS descriptions. A name match is not evidence, so it
  is opt-in and always outranked by published ranges.
- **Anonymizer coverage is Tor and whatever you supply.** The Tor Project's exit
  list is free and authoritative, and `--anonymizer` takes any list of addresses
  in the same shape. Commercial VPN and proxy space is not covered by any free
  source at scale, so `IsAnonymizer` being false means "not on a list we have",
  not "not a VPN".

## The `.eqatlas` format

Version 2 is a header, a section table, the sections, and a trailing CRC-32 over
everything before it. Readers skip section kinds they do not recognise, so later
datasets can carry new signals without breaking already-deployed readers.

Everything a file claims about itself is checked before it is believed: record
counts against the real file length, section bounds against the buffer, ranges
against their own ordering, and the whole body against its checksum. A corrupt
download fails loudly at load instead of answering confidently.

Version 1 files still load. `IpAtlasDatabase.TryOpen` reports failure instead of
throwing, for services whose fallback is to keep serving the dataset they have.

## Where the data comes from

Every source is published by the organisation it describes and is free to use.
Check the terms yourself before you ship — they are the load-bearing part of the
"no license-encumbered databases" claim:

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

## Upgrading from 1.x

Datasets written by 1.x still load. Datasets written by 2.x need 2.x to read.

`IpInfo` gained `Traits`, `Scope` and `Location`; construction by position still
compiles, deconstruction by position does not. The compiler's parsers now emit a
single `AtlasEntry` type and `DatasetBuilder` takes ranked layers
(`AddRegistry`, `AddAsns`, `AddGeofeed`, `AddCloud`, `AddOverrides`) instead of
two typed lists. `TravelAssessment` gained `Precision` and `Reason`, and now
answers `null` in cases 1.x answered `true` or `false` — see the list above,
which is the point of the change rather than a side effect of it.
