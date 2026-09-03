# CAN Vehicle Replay

Replays real recorded vehicle CAN traffic in step with the dash-cam video it was
recorded with, for an exhibition demonstration.

```
Windows PC  ──USB──▶  PEAK PCAN-USB  ──Classical CAN──▶  MCP2515
                                                            │ SPI
                                                            ▼
                                       Android  ◀──existing link──  Spresense
```

The Windows player shows the video and transmits the same drive's CAN onto the
bus. The Spresense forwards raw frames to Android, which decodes them with
definitions generated from opendbc and shows the vehicle's state.

**Only recorded vehicle frames are ever transmitted.** There is no control
channel to the Spresense and no invented CAN IDs — scenario changes are signalled
by a deliberate gap in traffic, which the Android side detects as staleness. The
claim "this bus carries nothing but traffic recorded from a real car" holds
literally.

---

## Getting the player

Three ways, in order of how little you have to do:

| | What you get | Needs |
|---|---|---|
| **It is in the clone** — `bin/CanReplayPlayer.exe` | The player, ~0.7 MB | [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Releases page** — `CanReplayPlayer-<version>-win-x64.exe` | The same player, standalone, ~59 MB | nothing |
| **Releases page** — `CanReplayPlayer-Setup-<version>-win-x64.exe` | Installer, Start-menu entry, one scenario | nothing |

`scripts/get_player.ps1` fetches the standalone build from Releases and writes it
over `bin/CanReplayPlayer.exe`, so `git status` will then show that file as
modified — that is expected, not a mistake.

The committed EXE is the framework-dependent build because it is the only one
small enough to belong in git history: a self-contained WPF build is ~59 MB, and
WPF cannot be trimmed. Release assets do not count towards repository size and
are not part of a clone, which is where the large builds live.

**Every build still needs the PEAK driver**, which is not redistributable here:
<https://www.peak-system.com/quick/DrvSetup>. See [INSTALLATION.md](INSTALLATION.md).

---

## Repository layout

| Path | What it is |
|------|-----------|
| `scenario_builder/` | Python. Converts comma2k19 segments into Scenario Packages. Build-time only. |
| `windows/` | C# / .NET 10 / WPF. The exhibition player. |
| `android/candecoder/` | Kotlin/JVM. Signal definitions, decoder, staleness. No Android dependencies, so it unit-tests on the JVM. |
| `android/app/` | Android Compose viewer. |
| `installer/` | Inno Setup script for the Setup.exe. |
| `scripts/build_release.ps1` | Tests → publish → portable ZIP → installer → checksums. |
| `scripts/get_player.ps1` | Downloads a built player from this repository's Releases. |
| `bin/CanReplayPlayer.exe` | The one build committed to git, so a clone is runnable. |
| `docs/scenario_package.md` | The Scenario Package and `.canbin` formats, and why they are shaped that way. |
| `THIRD_PARTY.md` | The few files here that came from elsewhere, and what is deliberately absent. |

`Scenarios/` (git-ignored) holds built packages.

---

## Architecture

### Windows

Six projects, with the Core deliberately free of any UI dependency
(requirement 15):

```
CanReplayPlayer.Core        PlaybackClock, CanTimeline, CanScheduler,
                            ICanTransport, Null/Memory transports,
                            IVideoPlayer, VideoDriftCorrector
CanReplayPlayer.Scenarios   scenario.json / playlist.json, ScenarioLibrary,
                            ReplaySession (all transport ordering lives here)
CanReplayPlayer.Pcan        PCAN-Basic.NET transport, detection, diagnostics
CanReplayPlayer.Video       MediaElement implementation of IVideoPlayer
CanReplayPlayer.Wpf         the UI, and nothing else
CanReplayPlayer.Tests       xUnit
```

**Synchronisation.** Neither medium is the master (requirement 11). A single
`PlaybackClock`, driven by a monotonic `Stopwatch`, owns scenario time; the video
player and the CAN scheduler both follow it. The clock has `Playing`, `Paused`,
`Stopped` and `Seeking` states, and only it advances time.

**CAN timing.** Each frame's deadline is absolute —
`runOrigin + frameTimestamp` — never a sleep per gap, so a late frame cannot push
the ones behind it (requirement 39). Windows' ~15.6 ms timer granularity is far
too coarse for a ~1,100 frame/second stream, so the wait sleeps only while
comfortably early and spin-waits the last 2 ms.

**Seeking.** Records in a `.canbin` are fixed width and time-ordered, so the file
is its own index and a seek is a binary search (requirement 10). No linear scan,
no separate index to build.

**Bus handling.** Buses are stored in separate files and never merged
(requirement 26). Exactly one selected bus is transmitted.

### Android

`candecoder` is plain Kotlin with no Android types, so the parts that must be
right — bit extraction, scaling, staleness — are unit-tested on the JVM without
an emulator. The app consumes it through a `CanSource` interface with two
implementations: a hardware source, and a replay source that reads the same
`.canbin` the player transmits. This repository ships the replay source only —
`CanSourceFactory.createHardwareSource()` returns null — because the bridge
firmware and its host-side library are whatever the integrator already uses. See
the "Adding a hardware source" note on `CanSource` for what an implementation
must guarantee.

---

## Getting comma2k19 — Chunk 1 only

comma2k19 is comma.ai's public dataset of ~33 hours of California highway
driving. Per its README:

* **Chunks 1–2 are the Toyota RAV4** (dongle `b0c9d2329ad1606b`)
* Chunks 3–10 are a Honda Civic

**You do not need the whole dataset.** It is a multi-file torrent of ten
independent `.zip` files, so a client can be told to fetch just the ones you
want:

| File | Size | Vehicle |
|------|-----:|---------|
| `Chunk_1.zip` | **8.73 GB** | Toyota RAV4 |
| `Chunk_2.zip` | **9.05 GB** | Toyota RAV4 |
| `Chunk_3.zip` … `Chunk_10.zip` | ~9.5 GB each | Honda Civic |
| *(whole torrent)* | *94.62 GB* | |

**Chunk 1 alone is enough** to get started — roughly 200 one-minute RAV4
segments, far more than an exhibition needs. That is 8.73 GB instead of 94.62 GB.

### Selective download

The dataset is on Academic Torrents:

```
https://academictorrents.com/details/65a2fbc964078aff62076ff4e103f18b951c5ddb
```

Download the `.torrent` from that page (or use the magnet link), then **before
starting the transfer**, deselect everything except `Chunk_1.zip`:

* **qBittorrent** — the Add Torrent dialog lists the ten files with checkboxes.
  Click *Select None*, tick only `Chunk_1.zip`, then Add.
  On an already-running torrent: the *Content* tab, right-click → *Do not
  download* on the others.
* **Transmission** — the "Files" list in the add dialog; untick all but Chunk 1.
* **Deluge** — the Files tab of the add dialog; set the others to *Skip*.
* **aria2** (headless) — `--select-file` takes 1-based indices in torrent order:

  ```bash
  aria2c --select-file=1 --seed-time=0 comma2k19.torrent
  ```

  Check the ordering first with `aria2c --show-files comma2k19.torrent`.

Then unzip:

```bash
unzip Chunk_1.zip -d /data/comma2k19/
```

You should get `/data/comma2k19/Chunk_1/<dongle>|<timestamp>/<segment>/`.

Please keep seeding what you downloaded — it is a community-hosted dataset.

**Do not commit any of it**; the builder reads it from wherever you put it.

> On Windows, note that route directories contain a `|`, which is not a legal
> filename character. Most archivers substitute `_`; the builder accepts both.

The comma2k19 repository also ships **one complete example segment** inside the
repo itself, at
`Example_1/b0c9d2329ad1606b|2018-08-02--08-34-47/40/`. That is enough to build a
real Scenario Package end to end without downloading the full dataset, and is
what this project's checked-in results were produced from.

> On Windows, `git clone` of comma2k19 fails to check out because `|` is not a
> legal filename character. Clone it anyway (the objects download fine) and
> extract the example with `git show`, or rename the route directory to use `_`.
> The builder accepts both spellings.

---

## Scenario Builder

Converts segments into Scenario Packages: extracts CAN from `raw_log.bz2`,
separates the buses, transcodes the video, generates signal definitions, and
validates the decode against comma2k19's own processed data.

### Install

```bash
cd scenario_builder
python -m pip install -e ".[dev]"
```

Needs Python 3.11+ and **FFmpeg on PATH** (build-time only — the player never
uses it).

### Get the DBCs

The builder needs opendbc's generated Toyota DBCs, which are produced from
templates rather than committed:

```bash
git clone https://github.com/commaai/opendbc.git
cd opendbc && PYTHONPATH=. python opendbc/dbc/generator/generator.py
```

That writes `opendbc/dbc/toyota_new_mc_pt_generated.dbc` and friends.

### Recommended workflow: analyse, then build what you picked

Chunk 1 holds roughly 200 segments. Transcoding all of them would take hours
and tens of gigabytes for a stand that shows maybe twenty, so the work is split
in two: measuring is cheap and repeatable, transcoding is not.

**1. Analyse the whole chunk** — reads each segment's CAN once and touches no
video:

```bash
python -m scenario_builder analyze \
    --input /data/comma2k19/Chunk_1 \
    --output scenario_analysis.json \
    --dbc-dir /path/to/opendbc/opendbc/dbc \
    --count 20
```

For every segment it measures duration, average / min / max / standard-deviation
of speed, steering range and activity, frame count, frames per second, unique
CAN IDs, per-bus frame counts, whether a thumbnail and video exist, and whether
`processed_log` is present for validation. All of it lands in
`scenario_analysis.json`.

It then recommends a shortlist across these categories:

| Category | Rule |
|----------|------|
| High Speed | average wheel speed ≥ 80 km/h |
| Cruise | `PCM_CRUISE.CRUISE_ACTIVE` set for ≥ 50% of the segment |
| Speed Changes | wheel speed spans ≥ 25 km/h |
| Steering Active | steering angle reaches ≥ 15° |
| Curves | steering angle standard deviation ≥ 5° |
| Slow Driving | average wheel speed ≤ 35 km/h |
| High CAN Rate | ≥ 2000 frames/s |
| Mixed | speed span ≥ 15 km/h **and** steering stdev ≥ 2° |

Selection round-robins across categories and enforces a minimum separation in
(speed, speed span, steering activity), so twenty near-identical motorway
minutes cannot crowd out the interesting ones. Every rule is a threshold on a
measured quantity — nothing infers road type or traffic, because the CAN does
not carry that.

**2. Build only what was recommended:**

```bash
python -m scenario_builder build-selected \
    --analysis scenario_analysis.json \
    --count 20 \
    --output ./Scenarios \
    --dbc-dir /path/to/opendbc/opendbc/dbc
```

The category each segment was picked for is written into its manifest as
`recommended_category` and added to its tags, so the generated playlists group
the way the analysis intended.

Edit `scenario_analysis.json` before this step to override the shortlist by
hand — it is ordinary JSON, and `recommendations` is just a ranked list.

### Building everything (or one segment)

```bash
python -m scenario_builder build \
    --input /data/comma2k19/Chunk_1 \
    --output ./Scenarios \
    --dbc-dir /path/to/opendbc/opendbc/dbc
```

Useful flags: `--limit N`, `--jsonl` (also write a readable CAN dump),
`--skip-video`, `--exclude-tx-echo`, `--prefix`, `--playback-bitrate`.

Other commands:

```bash
python -m scenario_builder inspect  --input <segment or chunk>   # summarise
python -m scenario_builder dbc-rank --input <segment>            # score DBCs
python -m scenario_builder verify   --input ./Scenarios          # check output
```

### Bit rates

A scenario carries **two** bit rates, deliberately never conflated:

| Field | Meaning | For comma2k19 |
|-------|---------|---------------|
| `original_bitrate` | the bus rate in the car the CAN was recorded from | **always `null`** |
| `playback_bitrate` | the rate this package is meant to be replayed at on the bench | 500000 by default |

comma2k19 publishes no bit rate and nothing in `raw_log.bz2` states one, so
`original_bitrate` stays null. Writing 500 kbit/s there because that is the
usual Toyota powertrain rate would be inventing a fact about the car.

`playback_bitrate` is a real, chosen number — the rate the PCAN-USB and the
MCP2515 are set to — and it is the one the player checks the interface against.
Change it with `--playback-bitrate`.

### Which DBC, and why

opendbc's own platform table maps `TOYOTA_RAV4` (RAV4 2016–2018, Toyota Safety
Sense P) to `toyota_new_mc_pt_generated` + `toyota_adas`. That mapping is not
taken on trust: every build re-decodes the recording with the selected DBC and
several alternates and compares each against comma2k19's `processed_log`,
writing the scores into `validation.json`.

On the example segment the selected DBC gives:

| Reference | Source | RMSE | Correlation |
|-----------|--------|-----:|------------:|
| speed | `WHEEL_SPEEDS` mean of 4 | 0.0086 m/s | 0.999993 |
| speed | `SPEED.SPEED` | 0.36 m/s | 0.99988 |
| steering angle | `STEER_ANGLE + STEER_FRACTION` | 0.017° | 0.99976 |
| wheel speed | `WHEEL_SPEED_FL` | 0.0146 m/s | 0.99998 |

(The steering angle is the sum of two signals because opendbc's own Toyota
`CarState` builds it that way; the coarse 1.5°-resolution `STEER_ANGLE` alone is
not the vehicle's wheel angle.)

The RAV4 **Hybrid** is ruled out separately: its hybrid-specific messages
(`0x163`, `0x2E6`, `0x2E7`, `0x33E`) are absent from the recording.

---

## Windows player

### Build and test

```bash
cd windows
dotnet build CanReplayPlayer.slnx
dotnet test  CanReplayPlayer.slnx
```

Requires the **.NET 10 SDK**. .NET 10 is the current LTS (support to November
2028); .NET 8 and 9 both reach end of support in November 2026, so new work
targets 10.

> If .NET 10 is installed under `%LOCALAPPDATA%\Microsoft\dotnet`, the `dotnet`
> on PATH will not see it — that muxer only finds SDKs under its own root. Use
> the full path, or set `DOTNET_ROOT`. `build_release.ps1` locates it for you.

### Release artifacts

```powershell
pwsh -File scripts/build_release.ps1 -Version 1.0.0
```

Runs every test suite, then produces:

```
release/
  portable/CanReplayPlayer.exe                     self-contained, single file, win-x64
  portable/Scenarios/
  framework-dependent/CanReplayPlayer.exe          ~0.7 MB; this is what bin/ carries
  CanReplayPlayer-portable-win-x64.zip
  installer/CanReplayPlayer-Setup-1.0.0-win-x64.exe
  assets/                                          bare files to attach to a GitHub Release
  checksums.txt
```

`release/assets/` holds the artifacts unzipped and version-stamped, ready to
upload: a release asset is downloaded directly, so wrapping an EXE in a ZIP only
adds a step for whoever fetches it.

The EXE is self-contained, so the exhibition PC needs **no .NET runtime**.
Scenarios sit beside it rather than inside it (requirement 56) — they are far
larger than the application.

The installer needs Inno Setup 6:
`winget install --id JRSoftware.InnoSetup --exact`.

### PEAK driver

The PCAN-Basic.NET NuGet package contains the **managed wrapper only**. The
native `PCANBasic.dll` and the PCAN-USB device driver come from PEAK's Windows
Driver Setup, which is **not** bundled here: PEAK's PCAN-Basic EULA permits free
redistribution of that package with its terms attached, but that is a different
artifact from the Driver Setup, whose redistribution terms are not stated in
anything shipped with the package. Requirement 60 says not to bundle what cannot
be confirmed, so the installer instead offers to open
<https://www.peak-system.com/quick/DrvSetup>.

The dependency is never hidden. On start-up the app checks for `PCANBasic.dll`
and, if absent, shows a banner reading "PCAN-USB Driver is not installed" with a
button to PEAK's page.

### Detection and troubleshooting

The status pill distinguishes: `Connected`, `Not Connected`, `Driver Missing`,
`PCAN-Basic Missing`, `Channel In Use`, `Bus Errors`, `BUS OFF`.

* **Driver Missing** — `PCANBasic.dll` was not found. Install the PEAK setup and
  press **Refresh**; no restart is needed.
* **Not Connected** — the driver is present but no PCAN-USB is attached.
* **Channel In Use** — another application (usually PCAN-View) holds the channel.
* **TEST CONNECTION** initialises the channel, reads its status and releases it.
  It transmits nothing (requirement 23).
* **Diagnostics** shows Windows and .NET versions, the PCAN-Basic.NET assembly
  version, the `PCANBasic.dll` path and version, the API version, every attached
  channel with its condition and driver version, and the scenario directory.
  **Copy Diagnostics** puts it all on the clipboard.

> A detail worth knowing: with no driver installed, PCAN-Basic.NET 5.1 does not
> raise `DllNotFoundException`. It raises its own exception saying it "could not
> be verified whether the current loaded native library PCANBasic.dll is genuine
> PEAK-System software", which is indistinguishable from a real tampering check.
> Detection therefore keys on whether the file exists, so "driver not installed"
> and "driver unhappy" stay distinguishable.

### Demo Mode

With **Transmit to PCAN-USB** unticked the app runs end to end — video, clock,
scheduler, statistics — against an in-memory transport that discards frames.
No PEAK hardware or driver needed (requirement 66).

### Adding scenarios and playlists

**Get Scenarios…** (next to the scenario list) downloads a comma2k19 chunk over
HTTPS from comma.ai's own Hugging Face mirror — resumable, so an interrupted
transfer continues rather than starting over. Only the two RAV4 chunks are
offered, because this build ships no Honda Civic signal definitions.

The download stops at the raw archive on purpose: converting it needs Python and
FFmpeg, which the player deliberately does not require. The window prints the
exact `analyze` and `build-selected` commands when it finishes.

Otherwise drop package folders into the Scenarios directory and press
**Reload**, or use **Change Folder…**. Playlists live in `Scenarios/playlist.json`:

```json
{
  "format_version": 1,
  "default_playlist": "featured",
  "playlists": [
    { "playlist_id": "featured", "title": "Featured",
      "description": "Booth loop", "scenario_ids": ["rav4_001", "rav4_002"] }
  ]
}
```

The builder generates one; edit it freely. IDs that no longer exist are dropped
with a warning rather than breaking Next/Previous.

---

## Android viewer

### Build

```bash
cd android
./gradlew :candecoder:test      # decoder tests, no SDK or emulator needed
./gradlew :app:assembleDebug    # APK
```

`local.properties`:

```properties
sdk.dir=C:\\Users\\you\\AppData\\Local\\Android\\Sdk
# optional: which Scenario Package to take signals.json and the demo timeline from
scenarioDir=C:\\path\\to\\Scenarios\\rav4_001
```

Set `CANREPLAY_SKIP_ANDROID_APP=1` to build only the decoder on a machine with no
Android SDK.

### Signal definitions, and what each build type contains

The app ships `signals.json` copied straight out of a built Scenario Package at
build time, so its definitions always match a real scenario rather than a
hand-maintained duplicate.

The recorded CAN fixture is treated differently:

| Asset | Debug | Release |
|-------|:-----:|:-------:|
| `profiles/…/signals.json` (~160 kB decode definitions) | yes | yes |
| `demo/bus_0.canbin` (~1.3 MB of recorded CAN) | yes | **no** |

A release APK carries **no CAN fixture at all**: raw frames can only come from
hardware. Pressing Replay in a release build is refused rather than silently
falling back, so an exhibition app cannot display values that did not come off
the bus. The Debug tab states which build is running.

### The hardware link

The reference bench puts a Spresense between the CAN bus and the phone: it reads
frames off the MCP2515 over SPI and forwards them to Android over the existing
USB link. Any bridge that can do that fits — implement `CanSource`, hand each
frame to the callback with a monotonic `SystemClock.elapsedRealtime()` timestamp,
and return it from `CanSourceFactory.createHardwareSource()`. Two constraints
matter: the callback runs on the source's own thread (a per-frame hop to the main
thread would put the recomposer permanently behind at ~1,100 frames/s), and
timestamps must be monotonic or staleness detection will read live signals as
stale.

No vendor SDK is bundled, and none is needed to build or test this repository.

### Staleness

There is no control channel, so the app infers playback state from traffic alone:
no frame for 250 ms means **STALE**, and each signal also expires after 3× its
message's cycle time (floored at 150 ms). A stale reading renders as `---`, never
as a number that stopped being true (requirement 53). The player's 350 ms
inter-scenario gap is comfortably above the detector and comfortably above the
slowest message period (~100 ms), so ordinary traffic never trips it.

---

## End-to-end test

**[`HARDWARE_TEST.md`](HARDWARE_TEST.md) is the full checklist** — driver,
channel detection, wiring, termination, and the 1 / 5 / 30 / 60 second timed
runs with a place to record every count. Use that on the bench.

In outline:

1. Build a Scenario Package from a comma2k19 RAV4 segment.
2. `dotnet test windows/CanReplayPlayer.slnx` — clock, seeking, scheduling,
   transitions, bus isolation, bench runs.
3. `./gradlew :candecoder:test` — bit extraction, staleness, counters, and an
   end-to-end test that decodes the built package and checks the result against
   the builder's own `validation.json`.
4. Install the PEAK driver; connect the PCAN-USB; start the player; confirm
   **Connected** and that **TEST CONNECTION** succeeds.
5. Wire PCAN-USB → MCP2515 → Spresense → Android at 500 kbit/s.
6. Run the timed tests from the player's **END-TO-END TEST** panel, shortest
   first, and compare the three devices' counts.

### Frames sent is not the same as frames delivered

`Api.Write` only queues a frame; it returns OK whatever happens on the wire. A
CAN bus needs a *second* node to acknowledge every frame, so transmitting with
nothing else attached produces thousands of "frames sent" while the controller's
error counter climbs and it eventually goes BUS OFF.

The player therefore reads the interface's own status every 250 ms while
transmitting, keeps the worst reading of the run, and leads the report with a
verdict. Measured on real hardware with a PCAN-USB and nothing else on the bus:

```
Frames scheduled : 1,102
Frames sent      : 1,102
Send errors      : 0
VERDICT          : FAILED -- the interface's error counter climbed while
                   transmitting, so the frames were not being acknowledged.
worst bus status : BusHeavy
```

Without the status poll that run reads as a flawless success.

### Comparing the three counts

Each device reports what it saw, so a shortfall can be localised to one link
rather than guessed at:

| Where | Figure | Where to read it |
|-------|--------|------------------|
| Windows | Frames scheduled / sent / errors, FPS, jitter | Timing panel, or **Copy Test Report** |
| Bridge | frames received, drops | its own console |
| Android | Raw CAN received, CAN rate, decoded signal updates | Debug tab |

Expect scheduled ≥ sent ≥ bridge ≥ Android. Where the count drops says what to
look at: within Windows means the PC could not transmit; between Windows and the
bridge means the CAN wiring, termination or bit rate; between the bridge and
Android means the forwarding link, and the bridge's own drop counter will confirm
it.

---

## Known limits

* Only comma2k19's single bundled example segment was available here, so one
  real Scenario Package is checked in. The builder's batch mode handles a whole
  chunk; the player's ability to carry 10–120 scenarios, auto-advance a
  ten-scenario playlist and keep buses isolated is covered by tests.
* A PCAN-USB was connected and exercised: detection, channel selection,
  `Initialize`/`GetStatus`/`Uninitialize` and **Test Connection** all pass
  against the real driver. The transmit path was run without a second node on
  the bus, which is what surfaced the ACK problem the verdict line now reports;
  confirming a clean transmit needs the full bench.
* comma2k19 states no CAN bit rate, so scenarios carry `bitrate: null` and the
  player warns instead of assuming.
* The Android app's on-device behaviour is covered by JVM tests of the decoder
  and by building the APK. It ships no hardware `CanSource`, so it has not been
  run against a physical bridge here.
