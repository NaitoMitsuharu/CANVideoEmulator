# Ubuntu CAN Endurance CLI

`can-replay-linux` replays a Scenario Package's CAN timeline to a Linux SocketCAN
interface. It has no video or GUI, so an Ubuntu PC can run a long PCAN-USB load test
independently of the Windows player.

## PCAN-USB setup

Install the PEAK Linux driver in SocketCAN/network-device mode. Confirm that the
adapter appears as `can0`, then configure the bitrate before starting the tool:

```bash
sudo ip link set can0 down
sudo ip link set can0 type can bitrate 500000
sudo ip link set can0 up
ip -details link show can0
```

The physical network still needs a powered normal-mode node for ACK, CAN-H/CAN-L/GND,
and exactly two 120 ohm terminators.

## Build and run

Install the .NET 10 SDK, copy the `Scenarios` directory to Ubuntu, then run:

```bash
dotnet run --project linux/src/CANVideoEmulator.Linux -- \
  --scenario-dir ./Scenarios \
  --scenario rav4_pcan_stress_400k_5min \
  --bus 0 --interface can0 --loop
```

Use `rav4_pcan_stress_450k_5min` or `rav4_pcan_stress_500k_5min` for the higher
load profiles. Stop a run with `Ctrl+C`; the CLI prints scheduled, accepted, error,
and P99 scheduling-jitter totals after each pass.