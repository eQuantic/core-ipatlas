# Changelog

## [2.1.0](https://github.com/eQuantic/core-ipatlas/compare/v2.0.0...v2.1.0) (2026-08-24)

### Features

* a supported way to write a dataset ([6847004](https://github.com/eQuantic/core-ipatlas/commit/684700477ab16c18d938c90e2561996c1c0d2662))
* publish a built world dataset ([23a70ca](https://github.com/eQuantic/core-ipatlas/commit/23a70caa3208065724b8515a042c845b1bfbaee0))

## [2.0.0](https://github.com/eQuantic/core-ipatlas/compare/v1.0.0...v2.0.0) (2026-08-24)

### ⚠ BREAKING CHANGES

* IpInfo carries flags, scope and location; TravelAssessment
carries precision and a reason; the compiler's parsers emit AtlasEntry and
DatasetBuilder takes layers rather than two typed lists. Datasets written
by this version need 2.x to read; datasets written by 1.x still load.

### Features

* fetch the registry dumps, and fix the CIDR the harvest writes ([71adc95](https://github.com/eQuantic/core-ipatlas/commit/71adc95a5e3c4b5ff0f313452d1724627aa189e6))
* harvest operator geofeeds and mark Tor exits ([54c9146](https://github.com/eQuantic/core-ipatlas/commit/54c9146261cdb70f4e5a7004797ed1a72c10f20a))
* layered sources, checksummed format and honest unknowns ([9ccd7cc](https://github.com/eQuantic/core-ipatlas/commit/9ccd7cc087e602addf84bd02916e1d250f651539))
* measure accuracy, resolve prefixes by specificity, fetch Azure ([f1161ad](https://github.com/eQuantic/core-ipatlas/commit/f1161adec557ff9481cb7182b14319e34afd790f))
* optionally trust the registry that a publisher holds more space ([d5eeece](https://github.com/eQuantic/core-ipatlas/commit/d5eeece9f1d597ced119514a1a569dbde4edf7cb))
* resolve overlaps per field, prove the AOT and speed claims ([9925bdf](https://github.com/eQuantic/core-ipatlas/commit/9925bdff3212c87e20640707e38bd4b22a2599ef))

### Bug Fixes

* write all five geofeed fields ([ea769d4](https://github.com/eQuantic/core-ipatlas/commit/ea769d4c2f638f7c80566ee30fe705f05b6bb388))

## 1.0.0 (2026-08-24)

### ⚠ BREAKING CHANGES

* package ids, namespaces, the dataset extension (.eqip is now
.eqatlas, magic ATLS) and the dotnet tool command (eqip is now eqatlas) all
change. Nothing was published under the old name, so no consumer is affected.

### Features

* IP intelligence core — .eqip dataset format, RIR compiler, lookups and travel-velocity math ([4e6d8e1](https://github.com/eQuantic/core-ipatlas/commit/4e6d8e1cb76dd4018c8b4429e407f54624ddc819))

### Code Refactoring

* rename eQuantic.IpIntel to eQuantic.IpAtlas ([18f6c7e](https://github.com/eQuantic/core-ipatlas/commit/18f6c7e31362a0680c6a5aa7040cb56e1a4e5ffb))
