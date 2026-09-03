# Scenario Package format

A Scenario Package is one ~1 minute comma2k19 segment, converted once by the
Scenario Builder into everything the Windows player and the Android viewer need
at runtime.

```
Scenarios/
  playlist.json
  rav4_001/
    scenario.json          manifest (requirement 33)
    video.mp4              H.264 in MP4, 1 s keyframes, faststart
    thumbnail.jpg          browser card image
    can/
      bus_0.canbin         one recorded bus per file, never merged
      bus_1.canbin
    dbc/
      toyota_new_mc_pt_generated.dbc   the DBC(s) used, copied verbatim
      toyota_adas.dbc
      signals.json         precomputed signal definitions for Android
    validation.json        decode checked against comma2k19 processed_log
    conversion.json        exactly how the video was transcoded
    debug/
      bus_0.jsonl          only with --jsonl; human-readable CAN dump
```

## `.canbin` — the CAN timeline

The player must seek without scanning (requirement 10) and must not re-parse
megabytes of JSON per scenario (requirement 32). Records are therefore fixed
width and time-ordered, which makes the file its own index: a seek is a binary
search over the records, with no side table to build or keep in sync.

All fields are little-endian.

### Header — 64 bytes

| Offset | Size | Field | Notes |
|-------:|-----:|-------|-------|
| 0  | 8 | magic | `43 41 4E 42 49 4E 00 00` (`"CANBIN\0\0"`) |
| 8  | 2 | `format_version` | currently `1`; a reader must refuse anything higher |
| 10 | 2 | `header_size` | `64` |
| 12 | 4 | `record_size` | `20` |
| 16 | 4 | `frame_count` | number of records that follow |
| 20 | 8 | `duration_us` | timestamp of the last record |
| 28 | 1 | `bus_index` | which recorded bus this file holds |
| 29 | 1 | `flags` | bit 0: the file contains TX-echo frames |
| 30 | 34 | reserved | zero |

### Record — 20 bytes, sorted by `timestamp_us` ascending

| Offset | Size | Field | Notes |
|-------:|-----:|-------|-------|
| 0  | 4 | `timestamp_us` | microseconds from scenario t=0 (max ≈ 71 minutes) |
| 4  | 4 | `can_id` | bit 31 set = 29-bit extended identifier |
| 8  | 1 | `dlc` | 0–8; classical CAN only (requirement 25) |
| 9  | 1 | `flags` | bit 0: TX echo, bit 1: RTR |
| 10 | 8 | `data` | bytes beyond `dlc` are zero |
| 18 | 2 | reserved | zero |

File size is exactly `64 + 20 × frame_count`.

The bus number is **not** stored per record. Each bus lives in its own file,
which keeps the record at 20 bytes and makes "transmit exactly one recorded bus"
the natural default rather than something the player has to filter for
(requirement 26).

### Implementations

Three, kept deliberately independent so a mistake in one cannot silently
propagate:

* `scenario_builder/scenario_builder/canbin.py` — writer and reader
* `windows/src/CANVideoEmulator.Core/Can/CanTimeline.cs` — reader, memory-mapped
  onto a `CanFrame` struct of exactly 20 bytes
* `android/candecoder/.../CanBinReader.kt` — reader

Change one and you must change all three; the tests in each pin the byte layout.

## Why the CAN comes from `raw_log.bz2`, not `processed_log`

comma2k19 offers `processed_log/CAN/raw_can/` as four numpy arrays (`t`,
`address`, `data`, `src`), which looks like a ready-made CAN timeline. It is not
usable as one: `data` has numpy dtype `|S8`, and that type **strips trailing NUL
bytes**.

Measured on the repository's own example segment:

* 520 of 135,468 frames read back as length 0
* the DLC is wrong for every frame whose payload ends in `0x00`

Requirement 70 forbids altering DLC or DATA, so the builder decodes
`raw_log.bz2` instead. Its capnp `CanData.dat` is a length-prefixed `Data` field
and is exact — parsing it yields the same 135,468 frames with a correct DLC
histogram (no zero-length frames; 96,666 eight-byte frames).

`processed_log` is still used, but only as the *reference* for validation.

## The `src` byte and TX echo

`CanData.src` is the panda's bus byte. From upstream:

* panda firmware `board/drivers/can.h` defines `#define CAN_BUS_RET_FLAG 0x80U`
  and pushes frames the panda **transmitted** with
  `(CAN_BUS_RET_FLAG | bus_number) << 4`, and frames it **received** with
  `bus_number << 4`
* openpilot `selfdrive/boardd/panda.cc` stores `(word >> 4) & 0xff` into `src`

So `src & 0x7F` is the physical bus and `src & 0x80` marks openpilot's own
transmissions. Both were physically on that bus, so the builder keeps both and
sets the record's TX-echo flag rather than dropping them — dropping them would
change what the bus looked like. `--exclude-tx-echo` opts out at build time, and
the player has a runtime toggle.

In the example segment: bus 0 has 53,800 received and 12,434 transmitted frames;
bus 1 has 45,000 and 24,234.

## `scenario.json`

Beyond the fields requirement 33 lists, two carry reasoning worth stating:

`video_can_offset_ms`
: Signed milliseconds from CAN t=0 to the **first video frame**, so
  `video_position = scenario_time − video_can_offset_ms / 1000`.
  Both media are timestamped on the device's boot-monotonic clock —
  `global_pose/frame_times` for the camera and `Event.logMonoTime` for CAN — so
  the offset is measured, not estimated. The example segment measures −37.472 ms.

`bitrate`
: `null` unless a bit rate is actually known. comma2k19 publishes none and
  nothing in `raw_log.bz2` states one, so guessing would be a fabrication
  (requirement 30). The player shows "unknown" and warns rather than silently
  transmitting at whatever the interface happens to be set to.

`default_bus` / `default_bus_reason`
: Chosen by a measurable rule — the bus carrying the most CAN IDs the scenario's
  DBC can decode, tie-broken by frame count — and the reason is written into the
  manifest so the choice can be checked. Buses are never labelled
  "Powertrain"/"ADAS"/etc. (requirement 28): nothing in the dataset documents
  what each captured bus was wired to.

`tags` / `tag_evidence`
: Every tag is a threshold on a quantity decoded from that scenario's own CAN,
  and the numbers behind it are recorded alongside so a tag can be traced back to
  why it applied (requirement 35). Judgements the CAN cannot support — "Traffic",
  road classification — are left for a human to add by editing the manifest.

## `signals.json`

Everything the Android app needs to decode a frame, precomputed so the phone
carries no DBC parser (requirement 46): bit geometry, sign, factor/offset,
limits, unit and value tables, per message.

Cycle time gets three fields rather than one, because collapsing them would be a
guess:

* `cycle_time_ms_dbc` — from the DBC's `BA_ "GenMsgCycleTime"`, usually `null`
  (most opendbc files carry no cycle-time attributes)
* `observed_cycle_time_ms` — the median inter-arrival time measured in this
  recording, with its sample count; a measurement of the recording, not a claim
  about the vehicle
* `effective_cycle_time_ms` + `effective_cycle_time_source` — what staleness
  actually uses, and which of the two (or the default) it came from

`undecoded_can_ids` lists frames present in the recording that the DBC does not
define. They are not dropped: requirement 55 requires them on the Raw CAN tab.
