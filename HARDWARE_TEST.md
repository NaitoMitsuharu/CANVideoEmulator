# Hardware bring-up checklist

For the first time real hardware is connected:

```
Windows PC ──USB──▶ PCAN-USB ──CAN──▶ MCP2515 ──SPI──▶ Spresense ──USB──▶ Android
```

Work top to bottom. Each section either passes or tells you where to stop —
a fault found at step 3 is far cheaper than the same fault found at step 11.

Print this, or copy it into a notes file and fill in the blanks as you go.

```
Date: ____________   Operator: ____________
Windows PC: ____________________________
PCAN-USB serial / device id: ____________
Scenario used: __________________________
```

---

## Part 1 — Windows and the PCAN-USB

### 1. PEAK driver

- [ ] PEAK-System Driver Setup installed (<https://www.peak-system.com/quick/DrvSetup>)
- [ ] **PCAN-Basic API** component was included
- [ ] `C:\Windows\System32\PCANBasic.dll` exists

> If the player shows **Driver Missing**, this step is not done. It keys on the
> presence of that DLL, because PCAN-Basic.NET does not raise a plain
> `DllNotFoundException` when it is absent.

Record: `PCANBasic.dll version ____________`

### 2. PCAN-USB connected

- [ ] Adapter plugged in **after** the driver install
- [ ] Windows Device Manager shows it with no warning triangle
- [ ] Status LED lit

### 3. Channel detection

Start `CanReplayPlayer.exe`, then press **Diagnostics**.

Under **PCAN devices**, record what `Api.GetAttachedChannels()` reported:

```
DeviceName        : ____________________   (expect "PCAN-USB")
ChannelHandle     : ____________________   (e.g. Usb01 (0x51))
DeviceID          : ____________________
ControllerNumber  : ____________________
ChannelCondition  : ____________________   (expect ChannelAvailable)
Channel driver    : ____________________
```

- [ ] Exactly one channel listed → the player selected it automatically
- [ ] Or several listed → the correct one chosen in the **CHANNEL** dropdown
- [ ] Status pill reads **Not Connected** (correct: nothing is open yet)

> **ChannelOccupied** means something else holds the channel — usually PCAN-View.
> Close it and press **Refresh**.

### 4. Hot-plug survives

- [ ] Unplug the PCAN-USB → the pill changes within ~2 s, no crash
- [ ] Plug it back in → it reappears without restarting the application
- [ ] **Refresh** re-detects on demand

### 5. TEST CONNECTION

Press **Test Connection**. It initialises the channel, reads its status with
`GetStatus`, and releases it. **It transmits nothing.**

- [ ] Reports success
- [ ] Status message records the bit rate

Record: `Result: ______________________________________`

> Confirm with a bus analyser or the adapter's TX LED that nothing was
> transmitted. If anything appeared on the bus here, stop — something is
> transmitting that should not be.

### 6. Bit rate

- [ ] Interface **BITRATE** set to **500000** (the default, matching the MCP2515 rig)
- [ ] Player shows no bit-rate warning for the chosen scenario

> The scenario's `playback_bitrate` is what this is checked against.
> `original_bitrate` is null for comma2k19 — the dataset does not document the
> vehicle's own bus rate, and it is never guessed.

---

## Part 2 — CAN wiring

### 7. Physical bus

- [ ] **CAN-H** — PCAN-USB pin 7 ↔ MCP2515 CAN-H
- [ ] **CAN-L** — PCAN-USB pin 2 ↔ MCP2515 CAN-L
- [ ] Ground reference between the two, if the boards are separately powered
- [ ] H and L not swapped (the most common cause of a silent bus)

### 8. Termination

- [ ] **120 Ω** at each physical end of the bus, two in total
- [ ] Measured across CAN-H/CAN-L with everything **powered off**: **≈60 Ω**

Record: `Measured resistance: ________ Ω`

> 120 Ω means only one terminator. ~40 Ω means three. Either will cause errors
> that look like a software problem.

### 9. Spresense and MCP2515

- [ ] Spresense powered and running the `can_controller` application
- [ ] MCP2515 bit rate **500 kbit/s**, matching the player
- [ ] MCP2515 crystal frequency matches the firmware's configuration
- [ ] Serial console open, so `can_controller:` lines are visible

> The timed tests need the bridge's frames-received counter readable on demand.
> If its firmware only prints that count when a frame is dropped, add an interval
> print before starting — otherwise the recv_count deltas below cannot be filled
> in.

### 10. Android

- [ ] Tablet/phone connected to the Spresense over the existing USB link
- [ ] CAN Replay Viewer installed and started
- [ ] Pressed **Connect** (not Replay)
- [ ] Status reads **WAITING** — correct: nothing is being transmitted yet

---

## Part 3 — Timed tests

Run in order. Each uses the player's **END-TO-END TEST** buttons, which
transmit the selected scenario from 0 s for a fixed time and then stop.

Before each run:

- [ ] Android: press **Stop** then **Connect** to zero its counters
- [ ] Spresense: note `recv_count` from the console
- [ ] Windows: tick **Transmit to PCAN-USB**

After each run, press **Copy Test Report** and paste it in, then fill in the
other two devices' figures.

### 11. One second

The question here is only *does anything arrive at all*.

```
Windows  scheduled ________  sent ________  errors ________  loss ______ %
         FPS ________  jitter P95 ________ ms
Spresense recv_count delta ________  drops ________
Android  raw received ________  CAN rate ________ /s  decoded updates ________
```

- [ ] **VERDICT line reads OK** — not FAILED or SUSPECT
- [ ] Windows send errors = 0
- [ ] Android left WAITING and reached **LIVE**
- [ ] Overview shows a plausible speed

> "Frames sent" alone is not a pass. `Api.Write` only queues a frame, so a bus
> with nothing to acknowledge still reports every frame as sent. The verdict line
> reflects the interface's own error counters, which is what actually moves when
> the frames are not getting through. A FAILED verdict with a full "frames sent"
> count almost always means the Spresense is not acknowledging: unpowered,
> mis-wired, un-terminated, or set to a different bit rate.

> Nothing on Android? The fault is between the PC and the Spresense: check
> steps 7–9 before anything else. Windows errors instead? Check bit rate and
> termination — **BUS OFF** means the frames are not being acknowledged.

### 12. Five seconds

```
Windows  scheduled ________  sent ________  errors ________  loss ______ %
         FPS ________  jitter P95 ________ ms
Spresense recv_count delta ________  drops ________
Android  raw received ________  CAN rate ________ /s  decoded updates ________
```

- [ ] Counts still line up
- [ ] Android CAN rate roughly matches the Windows FPS
- [ ] Gauges track the video

### 13. Thirty seconds

Long enough for buffers to fill and any drift to show.

```
Windows  scheduled ________  sent ________  errors ________  loss ______ %
         FPS ________  jitter P95 ________ ms  max ________ ms
Spresense recv_count delta ________  drops ________
Android  raw received ________  CAN rate ________ /s  decoded updates ________
```

- [ ] Spresense `drops` still 0
- [ ] Jitter P95 has not grown against the 5 s run
- [ ] No **BUS OFF**, no bus-error warnings

### 14. Sixty seconds

A whole scenario, which is what the exhibition actually runs.

```
Windows  scheduled ________  sent ________  errors ________  loss ______ %
         FPS ________  jitter P95 ________ ms  max ________ ms
Spresense recv_count delta ________  drops ________
Android  raw received ________  CAN rate ________ /s  decoded updates ________
```

- [ ] Runs to the end with no intervention
- [ ] Android stayed LIVE throughout

---

## Part 4 — Loss

For the 60 second run:

```
A = Windows frames scheduled   ________
B = Windows frames sent        ________
C = Spresense recv_count delta ________
D = Android raw received       ________

PC-side loss        (A - B) / A × 100 = ________ %
CAN link loss       (B - C) / B × 100 = ________ %
Spresense→Android   (C - D) / C × 100 = ________ %
End to end          (A - D) / A × 100 = ________ %
```

Expect **A ≥ B ≥ C ≥ D**. Where the drop happens says what to look at:

| Gap | Means | Look at |
|-----|-------|---------|
| A → B | Windows could not transmit | send errors, TX queue full, **BUS OFF**, bit rate, termination |
| B → C | Frames did not survive the bus | wiring, termination, MCP2515 bit rate and crystal, cable length |
| C → D | Spresense could not forward fast enough | Spresense `drops` counter, USB link, Android load |

A count going **up** somewhere is not noise — it means something other than the
player is transmitting on that bus. Find it before continuing.

- [ ] End-to-end loss recorded
- [ ] Any non-zero loss traced to a link

---

## Part 5 — Behaviour checks

With a scenario playing normally (not a timed test):

### 15. Pause and stale

- [ ] Press **PAUSE** → Windows stops transmitting immediately
- [ ] Android goes **STALE** within ~250 ms
- [ ] Values become `---`, not frozen numbers
- [ ] Press **PLAY** → returns to LIVE and resumes from the same position

### 16. Scenario change

- [ ] Press **Next** → about 350 ms of silence, then the next scenario
- [ ] Android went STALE during the gap and cleared the old values
- [ ] No value from the previous scenario survived into the new one

### 17. Seek

- [ ] Drag the seek bar → video and CAN both land at the new position
- [ ] Android goes STALE briefly, then LIVE again

### 18. Bus selection

- [ ] Change **CAN Bus** to another recorded bus
- [ ] Transmission pauses, then resumes with that bus's frames
- [ ] Android's Raw CAN tab shows a different set of CAN IDs

### 19. Shutdown

- [ ] Close the player normally
- [ ] Nothing further appears on the bus
- [ ] The PCAN-USB can be reopened by another application (the channel was released)

---

## Sign-off

```
All timed tests completed:        [ ] yes  [ ] no
End-to-end loss at 60 s:          ________ %
Jitter P95 at 60 s:               ________ ms
Issues found:
  ______________________________________________________
  ______________________________________________________

Signed: ______________________  Date: ______________
```

Attach the four **Copy Test Report** blocks and the Diagnostics output
(**Diagnostics → Copy Diagnostics**) to this sheet.
