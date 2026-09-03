# Third-party material in this repository

Only the items below are not original to this project.

## Cap'n Proto schemas — `scenario_builder/scenario_builder/schema/`

| File | Origin | Licence |
|------|--------|---------|
| `log.capnp`, `car.capnp` | [commaai/cereal](https://github.com/commaai/cereal) | MIT |
| `include/c++.capnp`, `include/java.capnp` | Cap'n Proto (Sandstorm Development Group) | MIT |

comma2k19's `raw_log.bz2` files are Cap'n Proto messages defined by cereal's
`log.capnp`, so the schema is needed to read them at all. The two files under
`include/` are imported by `log.capnp` and are not present in the cereal
repository; they carry their own MIT headers. Both schemas are copied verbatim
at a pinned upstream revision so the reader cannot drift from the definitions
that produced the recordings.

## Gradle wrapper — `android/gradle/wrapper/gradle-wrapper.jar`

Apache License 2.0, from the Gradle distribution. Committed as the Gradle
wrapper is designed to be.

---

## Things this repository does **not** contain

* **The PEAK PCAN driver and `PCANBasic.dll`.** PEAK does not permit
  redistribution, so they are installed separately from
  <https://www.peak-system.com/quick/DrvSetup>. Only the PCAN-Basic.NET NuGet
  package is referenced, and it is restored from nuget.org rather than vendored.
* **opendbc DBC files.** The Toyota DBCs are generated from templates rather
  than committed upstream; the Scenario Builder reads them from a local opendbc
  checkout at build time and copies the ones it used into each Scenario Package.
* **comma2k19 data.** Recorded video and CAN are downloaded by whoever builds
  scenarios; they are distributed by comma.ai under their own terms.
* **Any vehicle-manufacturer or board-vendor SDK.** The Android app talks to a
  CAN bridge through the `CanSource` interface and ships no implementation of
  one, so nothing proprietary is needed to build or test it.
