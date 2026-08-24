# eQuantic.IpAtlas documentation

Everything beyond the [project README](../README.md), which stays short on
purpose. Start wherever your question is.

## Understanding what it answers

| | |
|---|---|
| **[Accuracy](accuracy.md)** | Where the numbers come from, how to reproduce them, and what a registry delegation is worth on its own. Read this before trusting any geolocation library, including this one. |
| **[Data sources](sources.md)** | Every file a dataset is built from, who publishes it, what it licenses, and how often it changes. |
| **[Geofeeds](geofeeds.md)** | RFC 8805 and RFC 9092: how operators publish where their own addresses are, and why a feed is only believed for what the registry says its publisher holds. |
| **[Impossible travel](impossible-travel.md)** | The travel signal, why it has three answers rather than two, and every case where it declines to judge. |

## Using it

| | |
|---|---|
| **[.NET API](api.md)** | `IpAtlasDatabase`, `IpInfo`, scopes and traits, with the shapes a risk check actually needs. |
| **[CLI reference](cli.md)** | Every `eqatlas` command and flag. |
| **[Operations](operations.md)** | Running it in production: refreshing datasets without downtime, staleness, memory, thread safety, container sizing. |
| **[Upgrading from 1.x](upgrading.md)** | Every removed member, the behaviour changes worth reading twice, and what needs no edit at all. |

## Internals

| | |
|---|---|
| **[Dataset format](format.md)** | The `.eqatlas` binary layout, byte by byte, including how a reader validates a file before believing it. |
| **[Build pipeline](pipeline.md)** | How ranked sources become one non-overlapping dataset, and why each field resolves independently. |
| **[Contributing](contributing.md)** | Building, testing, benchmarking, and the public API surface file. |

## A note on the numbers

Every figure in these pages was measured, not estimated, and each page says
what it was measured on. Where something is a limitation rather than a result,
it is written as one. If a number here disagrees with what you measure, the
measurement wins — `eqatlas accuracy` and the benchmark project are in the
repository so you can check.
