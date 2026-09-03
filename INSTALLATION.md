# Setting up the exhibition PC

For whoever is setting up the stand. No programming knowledge needed.

You will need:

* a Windows 10 or Windows 11 computer, 64-bit
* a PEAK-System **PCAN-USB** adapter
* one of the downloads from the project's **Releases** page:
  * `CanReplayPlayer-Setup-...exe` — installer (recommended for the stand)
  * `CanReplayPlayer-...-win-x64.exe` — the player on its own, nothing to install
  * `CanReplayPlayer-portable-...zip` — the player plus one scenario

Allow about 20 minutes, most of it downloading the driver.

---

## Step 1 — Install the PCAN-USB driver

**Do this before plugging the adapter in.**

1. Go to <https://www.peak-system.com/quick/DrvSetup>
2. Download the **PEAK-System Driver Setup for Windows**.
3. Run it and accept the defaults. When it offers a list of components, make sure
   **PCAN-Basic API** is ticked — the application needs it.
4. Restart the computer if it asks.

> Why this is separate: PEAK does not permit us to include their driver in our
> installer, so it has to be downloaded from PEAK directly. This is a one-time
> step per computer.

---

## Step 2 — Connect the PCAN-USB

Plug the PCAN-USB into a USB port. Windows will install it automatically; a small
notification appears the first time.

The adapter has a status LED. A steady or slowly blinking light means Windows has
found it.

---

## Step 3 — Install the application

**Either** run `CanReplayPlayer-Setup-...-win-x64.exe` and follow the prompts,

**or**, if you were given the plain `CanReplayPlayer-...-win-x64.exe`, just put it
somewhere convenient such as `C:\CanReplay` — there is nothing to install and it
will run from a USB stick,

**or**, if you were given the ZIP, right-click it → **Extract All…** and put the
extracted folder in the same sort of place. The ZIP is the same player with one
scenario already beside it.

> Working from a `git clone` instead? `bin\CanReplayPlayer.exe` is already there.
> That one is small because it does not carry .NET inside it, so install the
> **.NET 10 Desktop Runtime (x64)** first:
> <https://dotnet.microsoft.com/download/dotnet/10.0>

> Windows may show "Windows protected your PC" because the file is not
> code-signed. Click **More info** → **Run anyway**. Check with whoever gave you
> the file if you are unsure.

Apart from the driver in Step 1, nothing else needs installing: the installer
and the plain EXE both carry everything they need.

---

## Step 4 — Check the Scenarios folder

Beside `CanReplayPlayer.exe` there must be a folder called **Scenarios**,
containing one folder per scenario:

```
CanReplayPlayer.exe
Scenarios\
    playlist.json
    rav4_001\
    rav4_002\
    ...
```

If the scenarios were supplied separately, copy them in here now. If they live
elsewhere, you can point the application at them in Step 6.

**No scenarios at all?** Start the application anyway and press
**Get Scenarios…** to the right of the (empty) scenario strip. It downloads
recorded driving data from comma.ai and tells you what to do next. Allow about
9 GB of download and 20 GB of free disk space.

---

## Step 5 — Start it

Double-click **CanReplayPlayer.exe** (or the Start-menu entry).

The first time, a short setup window appears and checks six things. Read what it
found and press **Start**. It will not appear again on this computer.

---

## Step 6 — Check the status bar

Along the top right you will see a coloured dot and a status:

| What it says | What it means | What to do |
|---|---|---|
| 🟢 **Connected** | Ready. | Nothing. |
| ⚪ **Not Connected** | Driver is fine, adapter is not plugged in. | Plug it in, press **Refresh**. |
| 🟡 **Driver Missing** | Step 1 was not completed. | Do Step 1, then press **Refresh**. |
| 🟡 **Channel In Use** | Another program is using the adapter. | Close PCAN-View, press **Refresh**. |
| 🔴 **BUS OFF** | Wiring or termination fault on the CAN bus. | Check the cable and the 120 Ω terminators. |

**Refresh** re-checks without restarting, so you can install the driver or plug
the adapter in with the application already open.

If the Scenarios folder is somewhere else, press **Change Folder…** at the right
of the Scenarios strip and select it.

---

## Step 7 — Play

1. Click a scenario card along the bottom.
2. Tick **Transmit to PCAN-USB** in the right-hand panel.
   *Leave it unticked to rehearse: the video plays and nothing goes onto the bus.*
3. Press **PLAY**.

The video plays and the recorded CAN goes out over the adapter in step with it.
The Android tablet should start showing speed, steering and the rest within a
second.

### The controls

| Control | What it does |
|---|---|
| **PLAY / PAUSE** | Start or freeze. Pausing stops CAN output too. |
| **Stop** | Stop and rewind to the beginning. |
| **◀◀ 10s / 10s ▶▶** | Jump ten seconds. |
| **Previous / Next** | Move through the playlist. |
| Seek bar | Drag to jump anywhere. |
| **PLAYLIST** | Which list Next and Previous follow. |
| **LOOP** | What happens at the end: next scenario, loop the list, or repeat one. |
| **CAN Bus** | Which recorded bus to transmit. Leave on the default. |

Space bar = play/pause. Left/right arrows = skip ten seconds.

By default the next scenario starts automatically, so the stand can be left
running unattended.

---

## If something goes wrong

**Nothing happens when I press PLAY**
Check a scenario card is selected (it has a blue outline). If the status bar
mentions a missing video or CAN file, that scenario's folder is incomplete.

**The video plays but the Android tablet shows nothing**
Check **Transmit to PCAN-USB** is ticked and the status shows **Connected**. Then
check the CAN wiring: CAN-H, CAN-L, and a 120 Ω terminator at each end.

**The Android tablet freezes on old values**
That is expected while playback is paused, seeking, or between scenarios — it
shows `---` because no CAN is arriving. It resumes on its own.

**The app shows a warning about bit rate**
The scenario was recorded at a different bit rate than the adapter is set to.
Change **BITRATE** in the right-hand panel to match, or untick transmit.

**It closed unexpectedly**
Press **Diagnostics**, then **Copy Diagnostics**, and paste it into an email to
whoever supplied the application. That text contains everything needed to
diagnose it. **Open Log Folder** on the same window has the detailed log.

---

## Packing down

Close the application normally (the ✕ button). It releases the PCAN-USB adapter
on exit, so it is safe to unplug afterwards.

Settings — chosen adapter, bit rate, playlist, window position — are remembered
for next time.
