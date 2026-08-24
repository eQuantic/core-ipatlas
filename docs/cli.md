# `eqatlas` reference

[← docs index](README.md)

```bash
dotnet tool install -g eQuantic.IpAtlas.Compiler
```

Six commands. Every one exits `0` on success, `1` on a real failure, and `2`
when the arguments are wrong — and a bad flag gets a sentence, not a stack
trace.

---

## `fetch` — download the public sources

```bash
eqatlas fetch --into <dir> [--attempts <n>] [--with-whois]
```

| flag | |
|---|---|
| `--into <dir>` | where to put them (default: current directory) |
| `--attempts <n>` | retries per file, with backoff (default 3) |
| `--with-whois` | also pull the registry database dumps the geofeed harvest reads (a few hundred megabytes) |

Downloads land in a temporary file and are renamed into place, so an
interrupted fetch cannot leave a half-written source for the next build to read
as truth. A failure in a required source fails the command; Azure is
best-effort and prints the manual step instead. See [Data sources](sources.md).

---

## `build` — compile a dataset

```bash
eqatlas build --out <dataset.eqatlas> [sources...]
```

| flag | layer | |
|---|---|---|
| `--rir <file>...` | registry | delegated-extended files |
| `--asn <file>...` | ASN | ip-to-ASN TSV |
| `--geofeed <file>...` | geofeed | RFC 8805 files |
| `--cloud <file>...` | cloud | AWS, Google or Azure range files |
| `--anycast <file>...` | cloud | plain CIDR lists, flagged anycast |
| `--anonymizer <file>...` | cloud | address lists, flagged anonymizer |
| `--override <file>...` | override | RFC 8805 files that outrank everything |

| flag | |
|---|---|
| `--out <path>` | required |
| `--source <text>` | recorded in the header, shown by `verify` |
| `--built-at <date>` | for reproducible builds |
| `--asn-heuristics` | guess hosting and mobile from AS names (off by default) |

At least one of `--rir`, `--geofeed` or `--cloud` is required. Sources are
ranked, not merged blindly — see [the pipeline](pipeline.md).

The build writes to a temporary file and renames it into place, so rebuilding
over the dataset a running service is serving cannot leave it truncated.

Output reports where every country came from, and what it had to reject:

```
    763,571 IPv4 ranges
    407,195 IPv6 ranges
     27,506 distinct places

  country by source:
     17,747 cloud provider
    468,584 operator geofeed
    683,742 registry delegation
```

---

## `geofeeds` — harvest operator geofeeds

```bash
eqatlas geofeeds --whois <dump.gz>... --out <geofeeds.csv>
```

| flag | |
|---|---|
| `--whois <file>...` | registry database dumps, gzipped or not |
| `--out <path>` | an RFC 8805 file, ready for `build --geofeed` |
| `--concurrency <n>` | feeds fetched at once (default 16) |
| `--timeout <seconds>` | per request (default 15) |
| `--attempts <n>` | retries, skipping what cannot succeed (default 3) |
| `--limit <n>` | bound the crawl, deterministically |
| `--same-org` | also accept prefixes the registry records against a publishing organisation |

A real crawl of thousands of hosts. Every prefix is checked against the
registry objects that pointed at its feed. See [Geofeeds](geofeeds.md).

---

## `verify` — check a dataset where it is deployed

```bash
eqatlas verify --dataset <file> [--max-age-days <n>]
```

```
  path      world.eqatlas
  format    version 2
  built     2026-08-24 00:00:00Z  (0.6 days ago)
  source    registries, ASNs, cloud providers, Tor
  ranges    763,571 IPv4, 407,195 IPv6
  places    27,506
  checksum  verified
```

Exits `1` if the dataset is corrupt, unreadable, or older than
`--max-age-days`. Suitable as a container health check or a deploy gate.

---

## `lookup` — answer for one or more addresses

```bash
eqatlas lookup --dataset <file> --ip <address>...
```

```
18.184.0.1
  scope     Public
  country   DE
  asn       16509
  traits    Hosting
  place     Frankfurt / eu-central-1 (50.11, 8.68)
  from      CloudProvider
```

Output is invariant-culture, so it is safe to parse.

---

## `accuracy` — score a dataset against ground truth

```bash
eqatlas accuracy --dataset <file> --truth <cloud-ranges.json>...
                 [--baseline <other.eqatlas>] [--min-correct <percent>]
```

Prints a Markdown table, and exits `1` below `--min-correct`. See
[Accuracy](accuracy.md), including why the `--baseline` column is the one that
measures something.

---

## Putting it together

```bash
eqatlas fetch --into sources --with-whois
eqatlas geofeeds --whois sources/*.db*.gz --out geofeeds.csv --same-org

eqatlas build \
  --rir sources/delegated-* \
  --asn sources/ip2asn.tsv \
  --cloud sources/*-ranges.json \
  --anycast sources/cloudflare-v4.txt sources/cloudflare-v6.txt \
  --anonymizer sources/tor-exits.txt \
  --geofeed geofeeds.csv \
  --out world.eqatlas

eqatlas verify --dataset world.eqatlas --max-age-days 1
```
