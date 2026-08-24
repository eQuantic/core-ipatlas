# Accuracy

[← docs index](README.md)

## The problem this library had

A regional internet registry records who was handed a block of addresses and in
which country the paperwork sat. It does not record where those addresses are
used, and for the networks that carry the most traffic the two are not close.
Amazon, Google and Microsoft register their address space to a single legal
entity — almost always in the United States — and run it on every continent.

Version 1.0 of this library was built entirely from registry delegation files.
Measured against the region each cloud provider publishes for its own prefixes,
that put roughly two addresses in three in the wrong country.

## Method

`eqatlas accuracy` samples one address from the middle of every prefix in a
provider's published range file, looks it up, and compares the answer to the
region the provider itself declared.

```bash
eqatlas accuracy --dataset world.eqatlas --baseline baseline.eqatlas \
  --truth sources/aws-ranges.json sources/gcp-ranges.json sources/azure-ranges.json
```

Ground truth is the provider's own file. That is the strongest free evidence
available about where cloud addresses are, and it is also the reason the
command prints a caveat with every result: **a dataset built from the same file
it is measured against is being checked for consistency, not accuracy.** The
`--baseline` column is the comparison that measures something.

## Results

53,909 sampled prefixes across AWS, Google Cloud and Azure:

| dataset | correct | wrong |
|---|---:|---:|
| registry delegations only | 33.0 % | 67.0 % |
| registries + provider range files | 100.0 % | 0.0 % |

Per provider, for the registry-only baseline:

| truth | samples | correct |
|---|---:|---:|
| `aws-ranges.json` | 14,489 | 36.0 % |
| `gcp-ranges.json` | 1,007 | 35.8 % |
| `azure-ranges.json` | 38,413 | 31.8 % |

## The holdout, which is the honest test

100 % is not a model that generalises. To show what it is, build a dataset from
the AWS and Google files only, then score it against Azure:

| dataset | scored against | correct |
|---|---|---:|
| registries only | Azure | 34.7 % |
| registries + AWS + Google | Azure | 34.7 % |

Identical. **Nothing transfers.** The accuracy comes entirely from ingesting
each provider's own file, and address space belonging to a provider whose file
you did not ingest is wrong about two times in three, exactly as before.

This is the single most important thing to understand about this library: it is
as accurate as the sources you give it, and the sources that matter are free.
`eqatlas fetch` collects all of them.

## What this does not measure

- **Residential and business networks.** No comparable ground truth exists for
  them at scale. For that space a registry delegation is usually right, because
  a small ISP's addresses really are used in the country it registered them in.
  The measurement above is deliberately taken where registry data is weakest.
- **City accuracy.** The table is country accuracy. City data comes from cloud
  regions and operator geofeeds, and the coverage figure — about 6 % of random
  routable addresses — is in [Geofeeds](geofeeds.md).
- **Anycast.** An address announced from thirty cities has no correct country.
  Those ranges carry a flag instead of a claim; see [the API guide](api.md).

## Reproducing it

```bash
eqatlas fetch --into sources

eqatlas build --rir sources/delegated-* --asn sources/ip2asn.tsv \
  --out baseline.eqatlas --source "registry delegations only"

eqatlas build --rir sources/delegated-* --asn sources/ip2asn.tsv \
  --cloud sources/*-ranges.json \
  --anycast sources/cloudflare-v4.txt sources/cloudflare-v6.txt \
  --anonymizer sources/tor-exits.txt \
  --out world.eqatlas --source "every layer"

eqatlas accuracy --dataset world.eqatlas --baseline baseline.eqatlas \
  --truth sources/*-ranges.json
```

`--min-correct <percent>` makes the command exit non-zero below a threshold, so
it can gate a pipeline. The repository runs this weekly against the live sources
(`.github/workflows/accuracy.yml`), which is how a publisher changing their
format surfaces here rather than in your fraud dashboard.

## Lookup performance

Measured on a full world dataset of 763,571 IPv4 and 407,195 IPv6 ranges:

| | |
|---|---|
| lookup, random routable addresses | 171 ns |
| allocation per lookup | 0 bytes |
| load, including checksum verification | 44 ms |

`benchmarks/` holds the BenchmarkDotNet project. On a synthetic 500,000-range
dataset a lookup is about 89 ns; real-world lookups cost more because random
addresses across a larger dataset miss cache, which is why the larger number is
the one quoted.
