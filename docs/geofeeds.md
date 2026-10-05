# Geofeeds

[← docs index](README.md)

City-level data outside the cloud providers' own ranges comes from geofeeds:
files operators publish saying where their own addresses are. This page is how
they are found, why most of what they claim is discarded, and what that says.

## The two RFCs

**RFC 8805** is the file. A CSV, one prefix per line:

```
198.51.100.0/24,PT,PT-11,Lisboa,1000-001
prefix,country,region,city,postal
```

No coordinates — the format does not carry them. Comment lines start with `#`,
and RFC 9632 signature blocks are skipped.

**RFC 9092** is how you find it, and it is the more important half. An operator
points at its feed from the registry object for the block:

```
inetnum:   5.222.0.0 - 5.223.255.255
netname:   DE-HETZNER-20120904
org:       ORG-HOA1-RIPE
geofeed:   https://www.hetzner.com/geofeed.csv
```

The older convention, `remarks: Geofeed <url>`, is read too.

## Why the pointer is the whole security model

A geofeed is a CSV on a web server, and the web server has no idea which
addresses its owner holds. Without checking, publishing a file and getting one
line into a registry object would be enough to relocate anybody's prefixes —
including someone else's.

**Every prefix is checked against the union of the registry objects that
pointed at its feed.** A claim outside them is discarded and counted.

This is not theoretical. From a full crawl:

```
   169,631 discarded, 13 kept  https://www.hetzner.com/geofeed.csv
    97,340 discarded, 167 kept  https://geofeed.cogentco.com/geofeed.csv
    86,151 discarded, 110 kept  https://api.cloudflare.com/warp-egress-ip-ranges.csv
```

## Harvesting

```bash
eqatlas fetch --into sources --with-whois
eqatlas geofeeds --whois sources/*.db*.gz --out geofeeds.csv
eqatlas build --rir sources/delegated-* --geofeed geofeeds.csv --out world.eqatlas
```

Parsing the dumps takes about eight seconds and 84 MB, streaming. The crawl
itself takes tens of minutes: thousands of small files on thousands of hosts.
Bound it with `--concurrency`, `--timeout`, `--attempts` and `--limit`.

A full run over the RIPE, APNIC and AFRINIC dumps:

```
    91,202 geofeed references across 5 registry dumps
     5,314 distinct feeds

     4,147 feeds read
       216 answered with something that is not a geofeed
       951 could not be reached

   534,447 prefixes accepted
 3,426,782 prefixes discarded: the feed had no registry object for them
```

The output is an ordinary RFC 8805 file, so it goes straight back in through
`--geofeed`, and two harvests of the same inputs produce the same file.

## Six prefixes in seven, discarded

That ratio says more about the state of geofeed publication than about the
check. Hetzner's feed lists 169,644 prefixes and is referenced from five
registry objects: one IPv4 /15 and four IPv6 /48s. The rest of their estate is
almost certainly theirs — but nothing in those five objects says so, and a tool
that assumed it would have to make the same assumption for a feed that is lying.

So the command names the feeds that overclaim rather than folding them into a
total. You can see which are operators who under-annotated and which are
something else.

## `--same-org`: one step further, still on the registry's word

Hetzner's five objects all carry `org: ORG-HOA1-RIPE`, and the registry records
dozens of further allocations against that same handle. The registry already
knows the rest of that space is theirs.

`--same-org` accepts prefixes the registry records against an organisation that
published the feed:

```bash
eqatlas geofeeds --whois sources/*.db*.gz --out geofeeds.csv --same-org
```

**The trust anchor stays the registry, not the feed.** A feed lying about
someone else's addresses still fails, because the `org:` handles will not match.
What changes is that a publisher no longer has to annotate every object to be
believed about space the registry already attributes to it.

Over the same 5,314 feeds:

| | strict RFC 9092 | `--same-org` |
|---|---:|---:|
| prefixes accepted | 534,447 | 3,572,264 |
| of those, on the registry's word | — | 3,037,449 |
| discarded | 3,426,782 (86.5 %) | 390,888 (9.9 %) |

The check still bites where it should. Cloudflare's WARP egress feed keeps 220
prefixes out of 86,261 either way, because that space is not registered to
Cloudflare under any handle it publishes from.

The widening is concentrated rather than uniform, and it is worth being precise
about what it buys. It multiplies the prefix count by nearly seven, but random
routable addresses answered with a city only move from 5.9 % to 6.2 %. What it
recovers is mostly hosting space — Hetzner, Cogent, GTT — where a lot of prefixes
cover comparatively few addresses. That happens to be where fraud traffic
concentrates, so the narrow gain is worth more than the number suggests, but it
is a narrow gain.

It is off by default, because it is one step past what RFC 9092 says, and
acceptances are counted by grounds and reported separately rather than merged:

```
  3,572,264 prefixes accepted, 3,037,449 of them on the registry's word that the same organisation holds them
```

Objects with no `org:` attribute contribute nothing to widen from.

## Registries with no bulk database

ARIN and LACNIC publish no bulk whois this can read, and that was written up
here as a permanent gap. It was wrong — out of reach *that way*, not out of
reach. Both run RDAP, and the delegated files already list every block, so
`eqatlas rdap` asks about each one:

```bash
eqatlas fetch --into sources
eqatlas rdap --delegated sources/delegated-arin sources/delegated-lacnic --out refs.csv
eqatlas geofeeds --references refs.csv --out geofeeds.csv --same-org
```

The reference is not where RFC 9092 puts it. The RFC describes a `geofeed`
attribute on the network object; ARIN carries it as free text in the remarks of
the *organisation entity* the network belongs to. The reader walks the whole
document rather than reading a field.

Queries route to the registry that answers. Asked about a Brazilian block:

| | response |
|---|---|
| LACNIC | `429 Too Many Requests`, 116 bytes |
| Registro.br | 12.6 kB, including the operator's geofeed |

Brazil is 64.8% of LACNIC's delegations, so routing them by the country code in
the delegated file both reaches data the regional registry will not give and
takes most of the load off one that plainly cannot carry it. Other national
registries advertise RDAP endpoints; of eight tested, five did not resolve and
two answered empty, so only the one that answers is used. An endpoint is not a
source until it answers.

## What this actually bought

Measured over a full ARIN crawl and a partial LACNIC one:

| | |
|---|---:|
| delegations asked about | 94,759 |
| geofeed references found | 4,690 |
| distinct feeds behind them | 889 |
| **feeds the bulk-whois harvest had not found** | **806** |
| prefixes those feeds contributed | 322,788 |
| city coverage, before | 6.28 % |
| city coverage, after | 7.03 % |

Worth being precise about the middle row, because it is easy to overstate. Four
thousand references are not four thousand feeds: one operator's file is pointed
at from hundreds of blocks. The gain is 806 feeds on 5,314 — about 15% more
sources, and three quarters of a percentage point of coverage.

The crawl stops short of complete. Registro.br began answering `403` after some
three thousand queries, so 21,939 Brazilian delegations remain. They are
reachable at a slower rate, and would not change the shape of the result.

## Where the coverage actually is

The 7% is an average of two populations that have nothing to do with each other:

| | city coverage |
|---|---:|
| hosting and datacenter space | **93.6 %** |
| everything else | 2.8 % |

That is the honest shape of what geofeeds provide. Operators who run
infrastructure publish; residential ISPs mostly do not. For deciding whether an
address is a datacenter and which one, this is close to solved. For placing a
residential subscriber in a city, no free first-party source exists and no
amount of further discovery will create one.

**Coordinates.** RFC 8805 carries country, region, city and postal code, and no
latitude or longitude. Geofeed-located ranges therefore answer with place names
and no coordinates, and travel assessment falls back to country precision for
them. Cloud provider ranges do carry coordinates, because a region is a place
this library has a table for.

## How much city coverage this buys

With a full harvest, about **6 % of random routable addresses** answer with a
city, and a world dataset goes from 154 distinct places to 27,506 — or 30,152
with `--same-org`. That number is a floor that rises as operators annotate more
of their objects, and you can raise it yourself for your own space with
`--override`.
