# Operations

[← docs index](README.md)

Running this in production: refreshing datasets, noticing staleness, sizing
containers, and what happens when something is wrong.

## Where the dataset lives

One file. Ship it in the image, mount it as a volume, or download it on start —
whatever you already do for static assets. A full world dataset is 17 MB
without geofeeds and 34 MB with them.

Whichever you choose, **verify it before serving from it**:

```bash
eqatlas verify --dataset /data/world.eqatlas --max-age-days 14
```

Exits `1` if the file is corrupt, unreadable, or older than the limit. That
makes it a usable container health check or deploy gate.

## Refreshing without downtime

The database is immutable, so a refresh is a reference swap. Readers in flight
finish against the old instance and it is collected when nothing holds it.

```csharp
public sealed class AtlasProvider
{
    private volatile IpAtlasDatabase _current;

    public IpInfo Lookup(IPAddress address) => _current.Lookup(address);

    public bool TryRefresh(string path, out string? error)
    {
        if (!IpAtlasDatabase.TryOpen(path, out var fresh, out error))
        {
            return false;   // keep serving what we have
        }

        _current = fresh!;
        return true;
    }
}
```

`TryOpen` is the important half. A refresh that throws on a truncated download
and takes the process with it is worse than one that keeps yesterday's data and
logs about it.

## Rebuilding in place is safe

`eqatlas build` writes to a temporary file beside the target and renames it.
A build that is killed, runs out of memory, or fills the disk cannot leave a
truncated file where a good one was — the rename is atomic, so a reader sees
either the old dataset or the new one.

This was not always true, and the failure it caused is the reason it is now:
an interrupted rebuild truncated the live dataset to zero bytes.

## Staleness

`BuiltAt`, `Age` and `Source` travel in the file header, so a stale dataset is
visible rather than silent.

```csharp
if (db.Age > TimeSpan.FromDays(14))
{
    _logger.LogWarning(
        "IP dataset is {Days:F0} days old, built from {Source}", db.Age.TotalDays, db.Source);
}
```

Registry delegations and cloud ranges change daily. A dataset a month old is
not wrong so much as increasingly out of date about who holds what, and cloud
providers reassign regions.

## Memory and sizing

| | without geofeeds | with geofeeds |
|---|---:|---:|
| file on disk | 17 MB | 34 MB |
| resident after load | 49 MB | 100 MB |
| load time | 30 ms | 44 ms |
| transient during load | ~file size | ~file size |

Loading reads the whole file into a pooled buffer, verifies the checksum, then
parses into arrays; the buffer is returned afterwards. Budget roughly twice the
file size for the load itself.

**Building is the expensive part** — about 1.1 GB peak for the larger dataset.
Do it somewhere else and ship the result. A 256 MB service container can serve
a dataset it could never have built.

## Threading

`IpAtlasDatabase` is immutable and safe to read from any number of threads. No
locks, no per-thread state, and a successful lookup allocates nothing, so there
is no allocation pressure to schedule around.

## What failure looks like

| situation | what happens |
|---|---|
| file missing or unreadable | `TryOpen` false with a message; `Open` throws `InvalidDataException` |
| truncated download | rejected at load: the section table runs past the end of the file |
| a flipped bit | rejected at load: checksum mismatch |
| a dataset from a newer version | rejected with a message naming the versions this build reads |
| a version 1 dataset | loads, reports `FormatVersion == 1`, no traits or locations |
| address not in the dataset | `IpInfo.Unknown`, `IsKnown` false |
| private or reserved address | `Scope` says which; never confused with "no data" |

## Automating the rebuild

```yaml
# nightly
- run: eqatlas fetch --into sources
- run: |
    eqatlas build --rir sources/delegated-* --asn sources/ip2asn.tsv \
      --cloud sources/*-ranges.json \
      --anycast sources/cloudflare-v4.txt sources/cloudflare-v6.txt \
      --anonymizer sources/tor-exits.txt \
      --geofeed geofeeds.csv \
      --out world.eqatlas
- run: eqatlas verify --dataset world.eqatlas --max-age-days 1
- run: eqatlas accuracy --dataset world.eqatlas --truth sources/*-ranges.json --min-correct 95
```

The geofeed harvest is separate and slower; weekly or monthly is proportionate.
`.github/workflows/accuracy.yml` in this repository is a working example of
both.

## Checking a dataset someone handed you

```bash
eqatlas verify --dataset theirs.eqatlas
eqatlas lookup --dataset theirs.eqatlas --ip 8.8.8.8 1.1.1.1 10.0.0.1
eqatlas accuracy --dataset theirs.eqatlas --truth aws-ranges.json
```

`verify` proves it is intact and says how old it is and what it claims to be
built from. `accuracy` says whether that claim holds up.
