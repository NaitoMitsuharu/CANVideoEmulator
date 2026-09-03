using System.Buffers.Binary;
using CANVideoEmulator.Core.Can;
using Xunit;

namespace CANVideoEmulator.Tests;

public class CanTimelineTests
{
    /// <summary>
    /// Build a .canbin image byte for byte, so these tests pin the wire format
    /// that scenario_builder/canbin.py writes rather than round-tripping our own
    /// assumptions.
    /// </summary>
    internal static byte[] BuildCanBin(int busIndex, params (uint TimeUs, uint Id, byte[] Data,
        bool Extended, bool TxEcho)[] frames)
    {
        var bytes = new byte[CanTimeline.HeaderSize + frames.Length * CanTimeline.RecordSize];
        "CANBIN\0\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 1);                       // version
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), CanTimeline.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), CanTimeline.RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)frames.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20),
            frames.Length == 0 ? 0 : frames[^1].TimeUs);
        bytes[28] = (byte)busIndex;
        bytes[29] = (byte)(frames.Any(f => f.TxEcho) ? 0x01 : 0x00);

        for (var i = 0; i < frames.Length; i++)
        {
            var offset = CanTimeline.HeaderSize + i * CanTimeline.RecordSize;
            var frame = frames[i];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), frame.TimeUs);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4),
                frame.Extended ? frame.Id | CanFrame.ExtendedIdFlag : frame.Id);
            bytes[offset + 8] = (byte)frame.Data.Length;
            bytes[offset + 9] = (byte)(frame.TxEcho ? CanFrame.FlagTxEcho : 0);
            frame.Data.CopyTo(bytes.AsSpan(offset + 10));
        }

        return bytes;
    }

    internal static CanTimeline Timeline(int busIndex,
        params (uint TimeUs, uint Id, byte[] Data, bool Extended, bool TxEcho)[] frames) =>
        CanTimeline.Parse(BuildCanBin(busIndex, frames));

    internal static CanTimeline EveryMillisecond(int count, int busIndex = 0)
    {
        var frames = Enumerable.Range(0, count)
            .Select(i => ((uint)(i * 1000), (uint)(0x100 + (i % 4)),
                new byte[] { (byte)i, 0x22 }, false, false))
            .ToArray();
        return Timeline(busIndex, frames);
    }

    [Fact]
    public void ParsesHeaderAndFrames()
    {
        var timeline = Timeline(0,
            (0, 0x0AA, [1, 2, 3, 4, 5, 6, 7, 8], false, false),
            (1500, 0x25, [9, 9, 9], false, false),
            (3000, 0x1FFFFFFF, [0xFF], true, true));

        Assert.Equal(3, timeline.Count);
        Assert.Equal(0, timeline.BusIndex);
        Assert.Equal(1, timeline.FormatVersion);
        Assert.True(timeline.ContainsTxEcho);
        Assert.Equal(TimeSpan.FromMilliseconds(3), timeline.Duration);
    }

    [Fact]
    public void PreservesIdDlcAndPayloadExactly()
    {
        var payload = new byte[] { 0x08, 0xFF, 0xFB, 0x00, 0x00, 0x00, 0x18, 0x84 };
        var timeline = Timeline(0, (0, 0x260, payload, false, false));

        var frame = timeline[0];
        Assert.Equal(0x260u, frame.CanId);
        Assert.Equal(8, frame.Dlc);
        Assert.Equal(payload, frame.ToArray());
        Assert.False(frame.IsExtended);
    }

    [Fact]
    public void TrailingZeroBytesSurvive()
    {
        // The exact failure that makes processed_log unusable as a CAN source.
        var timeline = Timeline(0, (0, 0x2E4, new byte[8], false, false));
        Assert.Equal(8, timeline[0].Dlc);
        Assert.Equal(new byte[8], timeline[0].ToArray());
    }

    [Fact]
    public void ZeroLengthFrameHasEmptyPayload()
    {
        var timeline = Timeline(0, (0, 0x123, [], false, false));
        Assert.Equal(0, timeline[0].Dlc);
        Assert.Empty(timeline[0].ToArray());
    }

    [Fact]
    public void ExtendedIdentifiersRoundTrip()
    {
        var timeline = Timeline(0, (0, 0x18DAF110, [0x01], true, false));
        Assert.True(timeline[0].IsExtended);
        Assert.Equal(0x18DAF110u, timeline[0].CanId);
        Assert.Equal("0x18DAF110", timeline[0].IdText());
    }

    [Fact]
    public void TxEchoFlagIsCarriedPerFrame()
    {
        var timeline = Timeline(0,
            (0, 0x0AA, [1], false, false),
            (10, 0x2E4, [1], false, true));

        Assert.False(timeline[0].IsTxEcho);
        Assert.True(timeline[1].IsTxEcho);
    }

    // --- seeking ---------------------------------------------------------

    [Fact]
    public void IndexAtOrAfterFindsTheFirstFrameAtOrAfterAPosition()
    {
        var timeline = EveryMillisecond(1000);

        Assert.Equal(0, timeline.IndexAtOrAfter(TimeSpan.Zero));
        Assert.Equal(0, timeline.IndexAtOrAfter(TimeSpan.FromMilliseconds(-5)));
        Assert.Equal(500, timeline.IndexAtOrAfter(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(501, timeline.IndexAtOrAfter(TimeSpan.FromMicroseconds(500_001)));
        Assert.Equal(1000, timeline.IndexAtOrAfter(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void SeekLandsOnTheExactBoundaryNotOneEitherSide()
    {
        var timeline = Timeline(0,
            (1000, 0x1, [0], false, false),
            (2000, 0x2, [0], false, false),
            (3000, 0x3, [0], false, false));

        Assert.Equal(0, timeline.IndexAtOrAfter(TimeSpan.FromMicroseconds(1000)));
        Assert.Equal(1, timeline.IndexAtOrAfter(TimeSpan.FromMicroseconds(1001)));
        Assert.Equal(1, timeline.IndexAtOrAfter(TimeSpan.FromMicroseconds(2000)));
        Assert.Equal(2, timeline.IndexAtOrAfter(TimeSpan.FromMicroseconds(2001)));
    }

    [Fact]
    public void SeekHandlesDuplicateTimestamps()
    {
        // Every frame inside one panda 'can' event shares a timestamp, so
        // duplicates are the normal case, not an edge case.
        var timeline = Timeline(0,
            (1000, 0x1, [0], false, false),
            (1000, 0x2, [0], false, false),
            (1000, 0x3, [0], false, false),
            (2000, 0x4, [0], false, false));

        Assert.Equal(0, timeline.IndexAtOrAfter(TimeSpan.FromMicroseconds(1000)));
        Assert.Equal(3, timeline.IndexAtOrAfter(TimeSpan.FromMicroseconds(1001)));
    }

    [Fact]
    public void SeekOnAnEmptyTimelineIsSafe()
    {
        var timeline = CanTimeline.Empty(0);
        Assert.Equal(0, timeline.Count);
        Assert.Equal(0, timeline.IndexAtOrAfter(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, timeline.Duration);
    }

    [Fact]
    public void SeekIsLogarithmic()
    {
        // A linear scan of 200k frames would be visible here; a binary search is not.
        var timeline = EveryMillisecond(200_000);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 20_000; i++)
        {
            timeline.IndexAtOrAfter(TimeSpan.FromMilliseconds(i % 200_000));
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
        Assert.True(elapsed < TimeSpan.FromSeconds(2),
            $"20,000 seeks over 200,000 frames took {elapsed.TotalMilliseconds:F0} ms, " +
            "which suggests the search is not O(log n)");
    }

    // --- rejection of bad files ------------------------------------------

    [Fact]
    public void RejectsBadMagic()
    {
        var bytes = BuildCanBin(0, (0, 1, [0], false, false));
        "NOTCANBN"u8.CopyTo(bytes);
        var error = Assert.Throws<CanTimelineException>(() => CanTimeline.Parse(bytes));
        Assert.Contains("not a .canbin", error.Message);
    }

    [Fact]
    public void RejectsUnsupportedFormatVersion()
    {
        var bytes = BuildCanBin(0, (0, 1, [0], false, false));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 99);
        var error = Assert.Throws<CanTimelineException>(() => CanTimeline.Parse(bytes));
        Assert.Contains("format_version 99", error.Message);
    }

    [Fact]
    public void RejectsTruncatedBody()
    {
        var bytes = BuildCanBin(0, (0, 1, [0], false, false), (10, 2, [0], false, false));
        var error = Assert.Throws<CanTimelineException>(
            () => CanTimeline.Parse(bytes.AsSpan(0, bytes.Length - 5)));
        Assert.Contains("truncated", error.Message);
    }

    [Fact]
    public void RejectsUnsortedFrames()
    {
        var bytes = BuildCanBin(0, (0, 1, [0], false, false), (10, 2, [0], false, false));
        // Rewrite the second record's timestamp so it precedes the first.
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(CanTimeline.HeaderSize + CanTimeline.RecordSize), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CanTimeline.HeaderSize), 10u);

        var error = Assert.Throws<CanTimelineException>(() => CanTimeline.Parse(bytes));
        Assert.Contains("not sorted", error.Message);
    }

    [Fact]
    public void RejectsDlcGreaterThanEight()
    {
        var bytes = BuildCanBin(0, (0, 1, [1, 2, 3, 4], false, false));
        bytes[CanTimeline.HeaderSize + 8] = 12;     // pretend it is CAN FD
        var error = Assert.Throws<CanTimelineException>(() => CanTimeline.Parse(bytes));
        Assert.Contains("classical CAN only", error.Message);
    }

    [Fact]
    public void RejectsAFileShorterThanTheHeader()
    {
        var error = Assert.Throws<CanTimelineException>(() => CanTimeline.Parse(new byte[10]));
        Assert.Contains("shorter than", error.Message);
    }

    [Fact]
    public void MissingFileGivesAClearMessage()
    {
        var error = Assert.Throws<CanTimelineException>(
            () => CanTimeline.Load(Path.Combine(Path.GetTempPath(), "no_such_bus.canbin")));
        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public void DistinctCanIdsAreSorted()
    {
        var timeline = Timeline(0,
            (0, 0x300, [0], false, false),
            (1, 0x100, [0], false, false),
            (2, 0x200, [0], false, false),
            (3, 0x100, [0], false, false));

        Assert.Equal([0x100u, 0x200u, 0x300u], timeline.DistinctCanIds());
    }
}
