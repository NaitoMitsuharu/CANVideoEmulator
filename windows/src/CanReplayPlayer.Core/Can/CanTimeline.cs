using System.Buffers.Binary;

namespace CanReplayPlayer.Core.Can;

/// <summary>
/// A recorded CAN bus loaded from one <c>.canbin</c> file, seekable in O(log n).
/// </summary>
/// <remarks>
/// Requirement 10 forbids scanning the timeline from the beginning to seek.
/// Records are fixed width and sorted by timestamp, so the file itself is the
/// index: <see cref="IndexAtOrAfter"/> is a binary search, and no side table
/// needs building or keeping in sync.
///
/// The format is defined in <c>scenario_builder/scenario_builder/canbin.py</c>;
/// the two implementations must be changed together.
/// </remarks>
public sealed class CanTimeline
{
    public const int HeaderSize = 64;
    public const int RecordSize = 20;
    public const ushort SupportedFormatVersion = 1;

    private static ReadOnlySpan<byte> Magic => "CANBIN\0\0"u8;

    private readonly CanFrame[] _frames;

    private CanTimeline(CanFrame[] frames, int busIndex, ushort formatVersion,
                        bool containsTxEcho)
    {
        _frames = frames;
        BusIndex = busIndex;
        FormatVersion = formatVersion;
        ContainsTxEcho = containsTxEcho;
        Duration = frames.Length == 0 ? TimeSpan.Zero : frames[^1].Timestamp;
    }

    public int BusIndex { get; }

    public ushort FormatVersion { get; }

    public bool ContainsTxEcho { get; }

    public int Count => _frames.Length;

    public TimeSpan Duration { get; }

    public ReadOnlySpan<CanFrame> Frames => _frames;

    public CanFrame this[int index] => _frames[index];

    public static CanTimeline Empty(int busIndex) =>
        new([], busIndex, SupportedFormatVersion, false);

    public static CanTimeline Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new CanTimelineException($"CAN timeline file not found: {path}");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException error)
        {
            throw new CanTimelineException($"could not read {path}: {error.Message}", error);
        }

        return Parse(bytes, path);
    }

    public static CanTimeline Parse(ReadOnlySpan<byte> bytes, string source = "<memory>")
    {
        if (bytes.Length < HeaderSize)
        {
            throw new CanTimelineException($"{source}: file is shorter than the 64 byte header");
        }

        if (!bytes[..8].SequenceEqual(Magic))
        {
            throw new CanTimelineException(
                $"{source}: not a .canbin file (bad magic). " +
                "Rebuild the scenario with the Scenario Builder.");
        }

        var formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        if (formatVersion != SupportedFormatVersion)
        {
            throw new CanTimelineException(
                $"{source}: .canbin format_version {formatVersion} is not supported by this " +
                $"build (which reads version {SupportedFormatVersion}). " +
                "Update the player, or rebuild the scenarios.");
        }

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        var recordSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (headerSize != HeaderSize || recordSize != RecordSize)
        {
            throw new CanTimelineException(
                $"{source}: unexpected header/record size {headerSize}/{recordSize}, " +
                $"expected {HeaderSize}/{RecordSize}");
        }

        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var busIndex = bytes[28];
        var fileFlags = bytes[29];

        var expected = (long)HeaderSize + (long)frameCount * RecordSize;
        if (bytes.Length < expected)
        {
            throw new CanTimelineException(
                $"{source}: truncated -- header declares {frameCount} frames " +
                $"({expected} bytes) but the file is {bytes.Length} bytes");
        }

        var frames = new CanFrame[frameCount];
        var body = bytes.Slice(HeaderSize, checked((int)(frameCount * RecordSize)));
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, CanFrame>(body)
            .CopyTo(frames);

        for (var i = 0; i < frames.Length; i++)
        {
            if (i > 0 && frames[i].TimestampMicroseconds < frames[i - 1].TimestampMicroseconds)
            {
                throw new CanTimelineException(
                    $"{source}: frames are not sorted by timestamp at index {i}; " +
                    "binary-search seeking would be wrong. Rebuild the scenario.");
            }

            if (frames[i].Dlc > 8)
            {
                throw new CanTimelineException(
                    $"{source}: frame {i} declares DLC {frames[i].Dlc}; " +
                    "this player transmits classical CAN only and will not truncate.");
            }
        }

        return new CanTimeline(frames, busIndex, formatVersion, (fileFlags & 0x01) != 0);
    }

    /// <summary>
    /// Index of the first frame at or after <paramref name="position"/>, or
    /// <see cref="Count"/> when the position is past the end. O(log n).
    /// </summary>
    public int IndexAtOrAfter(TimeSpan position)
    {
        if (position <= TimeSpan.Zero)
        {
            return 0;
        }

        var ticks = position.Ticks / 10L;
        if (ticks > uint.MaxValue)
        {
            return _frames.Length;
        }

        var target = (uint)ticks;
        int low = 0, high = _frames.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_frames[middle].TimestampMicroseconds < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Distinct identifiers on this bus, ascending.</summary>
    public uint[] DistinctCanIds()
    {
        var seen = new HashSet<uint>();
        foreach (var frame in _frames)
        {
            seen.Add(frame.CanId);
        }

        var ids = seen.ToArray();
        Array.Sort(ids);
        return ids;
    }
}

public sealed class CanTimelineException : Exception
{
    public CanTimelineException(string message) : base(message)
    {
    }

    public CanTimelineException(string message, Exception inner) : base(message, inner)
    {
    }
}
