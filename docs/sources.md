# Data sources

[← docs index](README.md)

Every source is published by the organisation it describes and is free to use.
"No license-encumbered databases" is the central claim of this library, so the
terms are the load-bearing part — check them yourself before you ship.

## What `eqatlas fetch` collects

| file | published by | gives | changes | build flag |
|---|---|---|---|---|
| `delegated-afrinic-extended-latest` | AFRINIC | delegation country | daily | `--rir` |
| `delegated-apnic-extended-latest` | APNIC | delegation country | daily | `--rir` |
| `delegated-arin-extended-latest` | ARIN | delegation country | daily | `--rir` |
| `delegated-lacnic-extended-latest` | LACNIC | delegation country | daily | `--rir` |
| `delegated-ripencc-extended-latest` | RIPE NCC | delegation country | daily | `--rir` |
| `ip2asn-combined.tsv.gz` | [iptoasn.com](https://iptoasn.com), from RouteViews | AS numbers | hourly | `--asn` |
| `ip-ranges.json` | Amazon Web Services | region per prefix | often | `--cloud` |
| `cloud.json` | Google Cloud | region per prefix | often | `--cloud` |
| ServiceTags | Microsoft Azure | region per prefix | weekly | `--cloud` |
| `ips-v4`, `ips-v6` | Cloudflare | anycast prefixes | rarely | `--anycast` |
| `torbulkexitlist` | The Tor Project | exit nodes | every 30 min | `--anonymizer` |

With `--with-whois`, it also collects the registry database dumps that
[the geofeed harvest](geofeeds.md) reads:

| file | published by | size |
|---|---|---:|
| `ripe.db.inetnum.gz` | RIPE NCC | ~220 MB |
| `ripe.db.inet6num.gz` | RIPE NCC | ~38 MB |
| `apnic.db.inetnum.gz` | APNIC | ~54 MB |
| `apnic.db.inet6num.gz` | APNIC | ~3 MB |
| `afrinic.db.gz` | AFRINIC | ~10 MB |

These stay opt-in because a nightly dataset rebuild has no use for them: the
geofeed harvest is a periodic job, not part of one.

## Notes on individual sources

### Registry delegation files

The five registries' own record of which blocks were delegated to which
country. Authoritative about delegation and about nothing else — see
[Accuracy](accuracy.md) for what that is worth on its own. Records with a
status other than `allocated` or `assigned`, or a country of `*`, are skipped.
Records describing a range that cannot exist are dropped and counted, not
wrapped into a corrupt one.

### ip-to-ASN

Derived from RouteViews, which is public-domain routing data. This library
takes the AS number and deliberately **ignores the country column**: it is
derived from the same registry delegations, so believing it would launder one
source through a second and make the dataset look corroborated when it is not.

The AS description is only read with `--asn-heuristics`, which is off by
default. A name match is not evidence.

### Cloud provider ranges

The reason this library is accurate at all. Each provider publishes which
region every prefix belongs to, and a region is a place —
`src/eQuantic.IpAtlas.Compiler/CloudRegions.cs` maps 180 of them to a country,
city and coordinates.

Azure is the awkward one: the file is behind a download page under a dated
name rather than at a fixed URL. `fetch` discovers it best-effort, and if
discovery fails it prints the manual step and carries on rather than failing
the whole fetch over one publisher's page layout.

A region the table has not learned yet still earns the hosting flag, because
"this is a datacenter" is a signal even without a location.

### Cloudflare

A plain list of anycast prefixes. They get `Hosting | Anycast` and no location,
which is the honest answer for an address announced from thirty cities.

### Tor exit list

The one anonymizer source that is both free and authoritative. It covers Tor
and nothing else. Commercial VPN and proxy space is not covered by any free
source at scale, so `IsAnonymizer` being false means "not on a list we have",
not "not a VPN".

### Operator geofeeds

Not fetched by default, because finding them means reading registry database
dumps and then crawling thousands of hosts. See [Geofeeds](geofeeds.md).

## Refresh cadence

Registry delegations and cloud ranges change daily; a nightly rebuild is
sensible and cheap. Geofeeds move much more slowly, and the harvest is
expensive, so weekly or monthly is proportionate.

Verify freshness where the dataset is deployed, not only where it was built:

```bash
eqatlas verify --dataset world.eqatlas --max-age-days 14
```

## What is missing, and why

- **ARIN and LACNIC bulk whois.** Not published under terms this can use, so
  geofeeds for North and South American space cannot be discovered.
- **Commercial VPN and proxy ranges.** No free source at scale.
- **Residential city-level data.** Only where an operator publishes a geofeed.
- **Coordinates from geofeeds.** RFC 8805 does not carry them.
