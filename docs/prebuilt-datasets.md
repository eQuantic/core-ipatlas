# Prebuilt datasets

[← docs index](README.md)

Building a dataset yourself takes a couple of seconds and a handful of
downloads. Harvesting operator geofeeds takes half an hour and reaches five
thousand hosts. If you want the second one and would rather not do the crawl,
a built dataset is published on a rolling release.

```bash
curl -fsSLO https://github.com/eQuantic/core-ipatlas/releases/download/dataset/world.eqatlas
curl -fsSLO https://github.com/eQuantic/core-ipatlas/releases/download/dataset/world.eqatlas.sha256
sha256sum -c world.eqatlas.sha256

eqatlas verify --dataset world.eqatlas
```

The URL is stable, so it works from a Dockerfile:

```dockerfile
ADD https://github.com/eQuantic/core-ipatlas/releases/download/dataset/world.eqatlas /data/
```

## Check it before you serve from it

`verify` is not ceremony. It confirms the checksum, that the file is internally
consistent, and how old it is — which matters more for a file you downloaded
than for one you built:

```bash
eqatlas verify --dataset world.eqatlas --max-age-days 45
```

The dataset is rebuilt monthly, so anything much past that means the pipeline
stopped and nobody noticed.

## What is in it, and how to check

Every release carries a `MANIFEST.md` recording the run that produced it: the
checksum, the build report, the geofeed harvest report, the accuracy score
against the cloud providers' published regions, and the sizes and dates of every
source file it was built from.

That is deliberate. A dataset someone hands you is exactly the kind of thing
that gets trusted more than one you built, so it should be the one that is
easiest to audit. If you want to check rather than trust:

```bash
eqatlas accuracy --dataset world.eqatlas --truth aws-ranges.json
```

## Why it is a prerelease, and rolling

**Prerelease** so it never becomes the repository's "Latest" release. That
belongs to the packages; a data file taking it would be confusing.

**Rolling** — one tag, replaced in place — because the point is a stable URL.
The trade is that it is not immutable: if you need a fixed input, keep the file
and its checksum rather than re-fetching, or build your own from
[the sources](sources.md), which are all public.

## Licensing

The dataset is derived from the files listed in [Data sources](sources.md),
each published by the organisation it describes and each free to use. Operator
geofeeds in particular are published under RFC 8805 precisely so that geolocation
consumers will read them.

Redistributing a *derived* dataset is a different question from building one for
yourself, and it is worth reading the terms of anything you depend on rather than
taking a README's word for it. If your situation makes that awkward, building
your own is two commands and no licence question at all:

```bash
eqatlas fetch --into sources --with-whois
eqatlas geofeeds --whois sources/*.db*.gz --out geofeeds.csv --same-org
```

## Building it yourself, the same way

The published dataset is produced by
[`.github/workflows/dataset.yml`](../.github/workflows/dataset.yml), which is
the same commands as [the CLI reference](cli.md) in a workflow. Nothing about
it is privileged: run the same commands and you get the same file, give or take
what the sources changed in between.

The workflow refuses to publish a dataset that fails verification or scores
below 95 % against the providers' own region files. A published dataset nobody
checked would be worse than none, because people would trust it more.
