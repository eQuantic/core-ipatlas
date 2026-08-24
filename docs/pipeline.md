# The build pipeline

[← docs index](README.md)

How several disagreeing sources become one sorted, non-overlapping dataset.

## Sources are ranked, not piled up

```
   override        your own corrections
      ▲
   cloud           AWS, Google, Azure, Cloudflare, Tor
      ▲
   geofeed         RFC 8805, published by the network's operator
      ▲
   ASN             AS numbers, and traits if you asked for heuristics
      ▲
   registry        delegation country: the floor everything else may correct
```

The order follows how close a source is to the network it describes. A registry
records who was handed a block; an operator records where its traffic comes out;
a cloud provider publishes which region a prefix runs in. When they disagree,
the one closer to the network is right.

## Each field resolves independently

Taking the whole answer from the highest-ranked source is not enough, because a
source can be specific about one thing and silent about another.

```
registry:  18.184.0.0/15 → US
cloud:     18.184.0.0/15 → DE, Frankfurt, 50.11/8.68, Hosting
ASN:       18.184.0.0/16 → AS16509

result:    18.184.0.1 → DE, Frankfurt, 50.11/8.68, AS16509, Hosting
```

- **country** from the highest-ranked source that names one
- **location** from the highest-ranked source that has one
- **ASN** from the highest-ranked source that has one
- **traits** accumulated across every source, because "datacenter" is true
  whoever noticed it

That last one matters: an anycast prefix says "datacenter, nowhere in
particular" without naming a country, and must not erase the only country
anyone stated.

## Inside one source: the most specific claim wins, per field

Published range files overlap themselves constantly. AWS lists the same prefix
once per service it powers, and publishes a /24 in Frankfurt inside a /12
marked GLOBAL. Azure publishes narrow prefixes for regions a build may not
recognise inside wider ones it does.

Whole-payload longest-prefix-match is wrong in both directions:

```
   /12  GLOBAL         (hosting, anycast, no country)
   /24  eu-central-1   (DE, Frankfurt)

   widest wins  → the /24's region is lost
   narrowest wins → in the mirror case, a known country is replaced by nothing
```

So resolution is per field there too: country from the smallest prefix naming a
country, coordinates from the smallest with coordinates, traits from all of
them. Neither prefix is wrong; each is specific about a different thing.

Getting this wrong cost several hundred regional blocks their country before it
was fixed, and it is why AWS and Google read back at 100 % now.

## The sweep

1. **Normalise each layer.** Collect every range boundary as a cut point, sort
   and dedupe, then walk them. At each point, fold the covering ranges into one
   entry, preferring the smallest prefix per field. The result is sorted and
   non-overlapping.
2. **Sweep across layers.** Collect the cut points of every normalised layer,
   and at each segment take each field from the highest-ranked layer that has
   one.
3. **Merge neighbours that agree**, so the output is as small as the data
   allows.
4. **Intern places and strings**, so the thousands of ranges in one cloud
   region share a single location record.

Deterministic throughout: ties break on a stable key, so two builds from the
same inputs produce byte-identical files given `--built-at`.

## Cost

A full world build — five registry files, ip-to-ASN, three cloud providers,
and 534,447 harvested geofeed prefixes:

| | |
|---|---|
| wall clock | 2.7 s |
| peak resident | ~1.1 GB |
| output | 34 MB |

It is a batch job that runs on a schedule, not something a service does. The
memory figure is worth knowing before you put it in a small container.

## What gets rejected, and reported

Nothing is dropped silently:

- registry records describing a range that cannot exist in its address family
- ranges with neither country, ASN, traits nor location
- geofeed prefixes the publisher had no registry object for
  ([Geofeeds](geofeeds.md))
- country codes that are not two letters A–Z

Each is counted and printed. A build that quietly discards a tenth of its input
and reports success is worse than one that fails.
