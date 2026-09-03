using System.Collections.ObjectModel;
using CANVideoEmulator.Core.Can;

namespace CANVideoEmulator.Wpf.ViewModels;

/// <summary>One row of the Recent CAN table.</summary>
public sealed record RecentCanRow(string Time, string Id, int Dlc, string Data, bool TxEcho);

/// <summary>
/// Shows the last N frames without letting a ~1 kHz stream drive the UI
/// (requirement 38).
/// </summary>
/// <remarks>
/// The scheduler pushes into a lock-free-ish ring from its own thread; the UI
/// drains that ring on a timer at <see cref="RefreshHz"/>. Nothing on the CAN
/// path touches an <see cref="ObservableCollection{T}"/> or the dispatcher, so a
/// slow repaint can never delay a frame. Frames that arrive between repaints are
/// simply overwritten in the ring -- the table is a live sample, not a log, and
/// pretending otherwise at 1,100 frames per second would either lie or stall.
/// </remarks>
public sealed class RecentCanMonitor
{
    /// <summary>Frames retained in the ring behind the display.</summary>
    public const int Capacity = 200;

    /// <summary>Number of whole rows that fit the current viewport.</summary>
    public int DisplayRows { get; private set; } = 0;

    public void SetDisplayRows(int count)
    {
        DisplayRows = Math.Clamp(count, 0, Capacity);
        Flush();
    }

    public const double RefreshHz = 15;

    private readonly CanFrame[] _ring = new CanFrame[Capacity];
    private readonly object _gate = new();
    private int _next;
    private int _filled;
    private long _observed;

    public ObservableCollection<RecentCanRow> Rows { get; } = [];

    /// <summary>Total frames seen, including ones overwritten before display.</summary>
    public long Observed
    {
        get { lock (_gate) { return _observed; } }
    }

    /// <summary>Called from the scheduler thread. Must stay allocation-free and fast.</summary>
    public void Add(in CanFrame frame)
    {
        lock (_gate)
        {
            _ring[_next] = frame;
            _next = (_next + 1) % Capacity;
            if (_filled < Capacity)
            {
                _filled++;
            }

            _observed++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _filled = 0;
            _observed = 0;
        }

        Rows.Clear();
    }

    /// <summary>Called on the UI thread by the repaint timer.</summary>
    public void Flush()
    {
        CanFrame[] snapshot;
        lock (_gate)
        {
            if (_filled == 0)
            {
                return;
            }

            // Newest first, and only as many as are displayed: building 200 rows
            // to show 12 would be wasted work 15 times a second.
            var take = Math.Min(_filled, DisplayRows);
            snapshot = new CanFrame[take];
            for (var i = 0; i < take; i++)
            {
                var index = (_next - 1 - i + Capacity * 2) % Capacity;
                snapshot[i] = _ring[index];
            }
        }

        for (var i = 0; i < snapshot.Length; i++)
        {
            var frame = snapshot[i];
            var row = new RecentCanRow(
                $"{frame.TimestampMicroseconds / 1_000_000.0:F3}",
                frame.IdText(),
                frame.Dlc,
                frame.DataHex(),
                frame.IsTxEcho);

            if (i < Rows.Count)
            {
                Rows[i] = row;
            }
            else
            {
                Rows.Add(row);
            }
        }

        while (Rows.Count > snapshot.Length)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }
}
