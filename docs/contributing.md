# Contributing

[← docs index](README.md)

## Layout

```
src/eQuantic.IpAtlas/             the runtime: format, lookups, scopes, travel
src/eQuantic.IpAtlas.Compiler/    the eqatlas tool: parsers, builder, commands
tests/eQuantic.IpAtlas.Tests/     runtime tests, on net8.0 and net10.0
tests/eQuantic.IpAtlas.Compiler.Tests/
tests/eQuantic.IpAtlas.AotSmoke/  publishes natively, proves the AOT claim
benchmarks/eQuantic.IpAtlas.Benchmarks/
```

## Building and testing

```bash
dotnet build eQuantic.IpAtlas.slnx -c Release
dotnet test eQuantic.IpAtlas.slnx -c Release
```

Warnings are errors, analyzers run at `latest-recommended`, and the runtime
library is tested on both target frameworks. A framework CI never executes is a
framework CI never covers.

```bash
dotnet run --project benchmarks/eQuantic.IpAtlas.Benchmarks -c Release -- --filter '*'
dotnet publish tests/eQuantic.IpAtlas.AotSmoke -c Release -o /tmp/aot && /tmp/aot/eQuantic.IpAtlas.AotSmoke
```

## The public API surface

`src/eQuantic.IpAtlas/PublicAPI.Unshipped.txt` records every public member.
Adding one without recording it fails the build (RS0016); removing or renaming
one also fails (RS0017). That is the point: a breaking change has to be written
down before it can compile.

Add new members to `PublicAPI.Unshipped.txt`. On release, its lines move into
`PublicAPI.Shipped.txt`.

Two things the analyzer will refuse that are worth knowing in advance:

- **Multiple overloads with optional parameters** (RS0026). Write an explicit
  overload pair instead, so the defaults a call site binds to cannot change
  when a signature grows.
- **Names ending in `Flags`** for a `[Flags]` enum (CA1711). Hence
  `NetworkTraits`.

## Writing tests

A few things this codebase learned the hard way:

- **Do not use RFC 5737 documentation ranges** (`203.0.113.0/24`,
  `192.0.2.0/24`, `198.51.100.0/24`) as stand-in addresses. They are classified
  as special-purpose before any dataset lookup happens, so a test using them
  will look like a lookup failure. Use ordinary public space.
- **Test the path the command runs.** Extracting a testable core and forgetting
  to call it from the command is a way to have green tests over dead code; it
  has happened here.
- **Test the malformed inputs.** A reader is only as trustworthy as the corrupt
  files it survives. `DatasetWriter` in the runtime tests builds datasets by
  hand — hostile counts, truncation, flipped bits, old layouts — precisely
  because the compiler would never produce them.

## Datasets in tests

`tests/eQuantic.IpAtlas.Tests/DatasetWriter.cs` writes `.eqatlas` files
directly, including version 1 files, so the reader can be tested against
anything. Fixtures for the parsers live in
`tests/eQuantic.IpAtlas.Tests/Fixtures/` and are shared with the compiler tests.

## Commits and releases

Commit messages follow `emoji type: description` (`✨ feat:`, `🐛 fix:`,
`♻️ refactor:`, `✅ test:`, `📝 docs:`, `👷 ci:`, `🔧 chore:`). semantic-release
reads them to compute the version, write the changelog, tag, and publish. A
`feat!:` or a `BREAKING CHANGE:` footer bumps the major.

## Changing the dataset format

The section table exists so that adding a section does not break deployed
readers. Adding one is a minor change; changing an existing record's layout is
not, and needs the version bumped with the old layout still readable. See
[the format](format.md).

Whatever you add, keep two properties: a reader must validate every claim a
file makes about itself before believing it, and two builds from the same
inputs must produce the same bytes.

## Adding a source

1. A parser producing `AtlasEntry` values.
2. A layer on `DatasetBuilder`, or an existing one if the rank is the same —
   see [the pipeline](pipeline.md) for what the ranks mean.
3. A flag on `build`, and an entry in `FetchCommand.Catalogue` if it has a
   stable URL.
4. A row in [Data sources](sources.md), including who publishes it and under
   what terms. That table is the evidence for the licensing claim, so a source
   without one does not belong in it.
5. If it changes what a dataset answers, a number in
   [Accuracy](accuracy.md) — measured, not estimated.
