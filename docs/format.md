# The `.eqatlas` format

[← docs index](README.md)

A dataset is one flat file: a header, a section table, the sections, and a
checksum. Version 2 is what this library writes; version 1 files still load.

## Why a section table

Version 1 was a fixed header followed by two blocks of fixed-size records.
Adding a field meant changing the record size, which meant every deployed
reader stopped working. That is a bad property for a file a service loads from
disk on a schedule.

Version 2 puts a table of sections in the header. **A reader skips section
kinds it does not recognise**, so a later dataset can carry new signals and an
older reader will still answer the questions it already knew how to answer.

## Layout

```
┌─────────────────────────────────────────────────┐
│ magic          uint32   'ATLS' (0x534C5441)     │
│ version        uint16   2                       │
│ flags          uint16   reserved, 0             │
│ builtAt        int64    unix seconds            │
│ sourceLength   uint16                           │
│ source         utf8[sourceLength]               │
│ sectionCount   uint8                            │
│ sections[sectionCount]                          │  21 bytes each:
│                                                 │    kind   uint8
│                                                 │    count  int32
│                                                 │    offset int64
│                                                 │    length int64
├─────────────────────────────────────────────────┤
│ section data, at the offsets the table gives    │
├─────────────────────────────────────────────────┤
│ crc32          uint32   over every byte before  │
└─────────────────────────────────────────────────┘
```

Multi-byte integers are little-endian, except IPv6 addresses, which are
big-endian so that byte order matches numeric order and a memcmp-style compare
sorts correctly.

## Sections

| kind | name | record | bytes |
|---:|---|---|---:|
| 1 | `V4Ranges` | start, end, country, asn, traits, location | 20 |
| 2 | `V6Ranges` | start, end, country, asn, traits, location | 44 |
| 3 | `Locations` | latitude, longitude, region offset, city offset | 16 |
| 4 | `Strings` | length-prefixed UTF-8 blob | — |

### Range records

```
IPv4 (20 bytes)                    IPv6 (44 bytes)
  start     uint32                   start     uint128 (big-endian)
  end       uint32                   end       uint128 (big-endian)
  country   uint16                   country   uint16
  asn       uint32                   asn       uint32
  traits    uint16                   traits    uint16
  location  uint32                   location  uint32
```

Both range sections are sorted by range start and are non-overlapping, which is
what makes a lookup one binary search.

- **country** is two ASCII letters packed into a `ushort`, `(first << 8) | second`.
  Zero means the range carries no country. Anything that is not two letters
  A–Z packs to zero rather than to mojibake.
- **traits** packs two things: the low byte is `NetworkTraits` (hosting,
  anycast, mobile, satellite, anonymizer), the high byte is `LocationSource`
  (registry delegation, geofeed, cloud provider, override). Provenance travels
  with every range because "Germany, because Amazon said so" and "Germany,
  because a registry recorded it in 2012" are different facts.
- **location** is a 1-based index into the `Locations` section. Zero means none.

### Locations and strings

```
Location (16 bytes)
  latitude      float32   NaN when the source carried no coordinates
  longitude     float32   NaN when the source carried no coordinates
  regionOffset  uint32    1-based offset into Strings, 0 for none
  cityOffset    uint32    1-based offset into Strings, 0 for none
```

The strings blob is a sequence of `length uint8` followed by that many UTF-8
bytes. Offsets are 1-based so that zero can mean "no string". Places are
interned, so the thousands of ranges in one cloud region share a single record.

## What a reader checks before believing a file

A dataset is an input like any other, and one that arrives over a network and
sits on a disk for months. `IpAtlasDatabase.Open` validates all of this before
answering a single lookup:

| check | what it prevents |
|---|---|
| magic and version | reading something that is not a dataset, or one from the future |
| declared length against the real file length | a header claiming two billion records exhausting memory |
| record counts against `MaxRecordCount` | the same, before any allocation happens |
| section offsets and lengths against the buffer | a section pointing outside the file |
| section length against `count × recordSize` | a truncated or padded section |
| CRC-32 over the whole body | silent bit rot answering confidently |
| range starts strictly ascending | binary search over unsorted data returning wrong answers |
| every range's end at or after its start | a corrupt record that silently matches nothing |

Failures raise `InvalidDataException` with a sentence saying which check failed.
`IpAtlasDatabase.TryOpen` returns false and the message instead, for services
whose fallback is to keep serving the dataset they already have.

## Version 1 compatibility

Version 1 files have no section table and no checksum: a fixed header, then a
record count for each family, then fixed-size records of 14 and 38 bytes with
no traits and no location. They still load, and report `FormatVersion == 1`.

Datasets written by version 2 need a 2.x reader. The version check says so in
the exception message rather than failing obscurely.

## Reproducibility

Two builds from the same inputs produce byte-identical files, provided
`--built-at` is given (otherwise the header carries the current time). Sources
are sorted and overlaps resolved deterministically, with stable tie-breaks. A
dataset you cannot reproduce is a dataset you cannot audit.
