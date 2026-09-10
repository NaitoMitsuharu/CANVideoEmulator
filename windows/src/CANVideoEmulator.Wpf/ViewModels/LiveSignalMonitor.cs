using CANVideoEmulator.Core.Can;
using CANVideoEmulator.Scenarios;

namespace CANVideoEmulator.Wpf.ViewModels;

/// <summary>
/// Decodes a handful of named signals (speed, steering, brake, gas, cruise) from
/// the live frame stream, using the scenario's own matched DBC. Same threading
/// shape as <see cref="RecentCanMonitor"/>: the scheduler thread updates raw
/// values through <see cref="OnFrame"/>, and the UI thread reads a formatted
/// snapshot on its own timer.
/// </summary>
/// <remarks>
/// The signal names mirror <c>scenario_builder/scenario_builder/manifest.py</c>'s
/// <c>auto_tags</c> exactly, so a scenario shows here precisely what already
/// justified its tags. Toyota and Honda profiles do not share message names
/// beyond WHEEL_SPEEDS (see the auto-tag code's comment on that), so most
/// vehicles only light up a subset -- the same graceful-degradation shape as the
/// IMU/GNSS overlay when a stream is absent.
/// </remarks>
public sealed class LiveSignalMonitor
{
    private const int SpeedSlot = 0;
    private const int SteerAngleSlot = 1;
    private const int SteerFractionSlot = 2;
    private const int BrakeSlot = 3;
    private const int GasSlot = 4;
    private const int CruiseSlot = 5;
    private const int SlotCount = 6;

    private readonly record struct Watch(SignalDefinition Signal, int Slot);

    private readonly object _gate = new();
    private readonly double?[] _values = new double?[SlotCount];
    private Dictionary<uint, List<Watch>> _byCanId = new();
    private bool _hasTable;

    /// <summary>
    /// Load the scenario's signal table, or clear the ticker when
    /// <paramref name="table"/> is null (no DBC match, or a non-default bus is
    /// selected -- the table was only scored against the default bus).
    /// </summary>
    public void LoadTable(SignalTable? table)
    {
        var byCanId = new Dictionary<uint, List<Watch>>();

        void Watch(string message, string signal, int slot)
        {
            if (table is null || !table.TryGetSignal(message, signal, out var msg, out var sig))
            {
                return;
            }

            if (!byCanId.TryGetValue(msg.CanId, out var list))
            {
                byCanId[msg.CanId] = list = [];
            }

            list.Add(new Watch(sig, slot));
        }

        Watch("WHEEL_SPEEDS", "WHEEL_SPEED_FL", SpeedSlot);
        Watch("STEER_ANGLE_SENSOR", "STEER_ANGLE", SteerAngleSlot);
        Watch("STEER_ANGLE_SENSOR", "STEER_FRACTION", SteerFractionSlot);
        Watch("BRAKE_MODULE", "BRAKE_PRESSED", BrakeSlot);
        Watch("GAS_PEDAL", "GAS_PEDAL", GasSlot);
        Watch("PCM_CRUISE", "CRUISE_ACTIVE", CruiseSlot);

        lock (_gate)
        {
            _byCanId = byCanId;
            _hasTable = table is not null;
            Array.Clear(_values);
        }
    }

    /// <summary>Called from the scheduler thread. Must stay allocation-free and fast.</summary>
    public void OnFrame(in CanFrame frame)
    {
        // _byCanId is only ever replaced wholesale (LoadTable), never mutated in
        // place, so reading the reference without the gate is safe and keeps the
        // ~1 kHz common case (a frame nothing here watches) lock-free.
        if (!_byCanId.TryGetValue(frame.CanId, out var watches))
        {
            return;
        }

        foreach (var watch in watches)
        {
            var value = SignalTable.Decode(frame.Data, watch.Signal);
            if (value is null)
            {
                continue;
            }

            lock (_gate)
            {
                _values[watch.Slot] = value;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_values);
        }
    }

    public readonly record struct Reading(bool HasAny,
        bool HasSpeed, string SpeedText,
        bool HasSteering, string SteeringText,
        bool HasBrake, string BrakeText,
        bool HasGas, string GasText,
        bool HasCruise, string CruiseText);

    /// <summary>Called on the UI thread by the repaint timer.</summary>
    public Reading Read()
    {
        bool hasTable;
        double?[] snapshot;
        lock (_gate)
        {
            hasTable = _hasTable;
            snapshot = (double?[])_values.Clone();
        }

        var speed = snapshot[SpeedSlot];
        var steerAngle = snapshot[SteerAngleSlot];
        var steerFraction = snapshot[SteerFractionSlot];
        var brake = snapshot[BrakeSlot];
        var gas = snapshot[GasSlot];
        var cruise = snapshot[CruiseSlot];

        var hasSteering = steerAngle is not null;
        var steeringDeg = (steerAngle ?? 0) + (steerFraction ?? 0);

        return new Reading(
            hasTable,
            speed is not null, speed is { } s ? $"{s:F0}" : "--",
            hasSteering, hasSteering ? $"{steeringDeg:F1}" : "--",
            brake is not null, brake is { } b ? (b != 0 ? "ON" : "--") : "--",
            gas is not null, gas is { } g ? $"{g:F0}" : "--",
            cruise is not null, cruise is { } c ? (c != 0 ? "ON" : "--") : "--");
    }
}
