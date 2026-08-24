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

It is off by default, because it is one step past what RFC 9092 says, and
acceptances are counted by grounds and reported separately rather than merged:

```
   prefixes accepted, N of them on the registry's word that the same organisation holds them
```

Objects with no `org:` attribute contribute nothing to widen from.

## What is out of reach

**ARIN and LACNIC** do not publish bulk whois under terms this can use, so
their operators' geofeeds cannot be discovered this way. That is a real gap in
coverage of North and South American address space, not an oversight.

**Coordinates.** RFC 8805 carries country, region, city and postal code, and no
latitude or longitude. Geofeed-located ranges therefore answer with place names
and no coordinates, and travel assessment falls back to country precision for
them. Cloud provider ranges do carry coordinates, because a region is a place
this library has a table for.

## How much city coverage this buys

With a full harvest, about **6 % of random routable addresses** answer with a
city, and a world dataset goes from 154 distinct places to 27,506. That number
is a floor that rises as operators annotate more of their objects, and you can
raise it yourself for your own space with `--override`.
