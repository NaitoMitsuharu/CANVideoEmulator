using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CanReplayPlayer.Core.Can;

/// <summary>
/// One classical CAN frame from a recorded bus.
/// </summary>
/// <remarks>
/// Requirement 25: classical CAN only, so <see cref="Dlc"/> is 0..8 and there is
/// no CAN FD state. Requirement 70: the identifier, DLC and payload are exactly
/// what the vehicle put on the wire; nothing in the player rewrites them.
///
/// This is a 20-byte struct laid out to match one <c>.canbin</c> record, so a
/// timeline is read by reinterpreting the file's bytes rather than allocating an
/// object per frame -- a one-minute segment holds ~70,000 frames per bus.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 20)]
public readonly struct CanFrame : IEquatable<CanFrame>
{
    /// <summary>Bit 31 of the stored identifier marks a 29-bit extended frame.</summary>
    public const uint ExtendedIdFlag = 0x8000_0000u;

    public const byte FlagTxEcho = 0x01;
    public const byte FlagRemoteRequest = 0x02;

    private readonly uint _timestampMicroseconds;
    private readonly uint _rawId;
    private readonly byte _dlc;
    private readonly byte _flags;
    private readonly byte _d0;
    private readonly byte _d1;
    private readonly byte _d2;
    private readonly byte _d3;
    private readonly byte _d4;
    private readonly byte _d5;
    private readonly byte _d6;
    private readonly byte _d7;
    private readonly ushort _reserved;

    public CanFrame(uint timestampMicroseconds, uint canId, bool extended, byte dlc,
                    ReadOnlySpan<byte> data, byte flags = 0)
    {
        if (dlc > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(dlc),
                $"classical CAN allows at most 8 data bytes, got {dlc}");
        }

        if (data.Length < dlc)
        {
            throw new ArgumentException($"need {dlc} data bytes, got {data.Length}", nameof(data));
        }

        _timestampMicroseconds = timestampMicroseconds;
        _rawId = extended ? canId | ExtendedIdFlag : canId;
        _dlc = dlc;
        _flags = flags;
        _reserved = 0;

        _d0 = dlc > 0 ? data[0] : (byte)0;
        _d1 = dlc > 1 ? data[1] : (byte)0;
        _d2 = dlc > 2 ? data[2] : (byte)0;
        _d3 = dlc > 3 ? data[3] : (byte)0;
        _d4 = dlc > 4 ? data[4] : (byte)0;
        _d5 = dlc > 5 ? data[5] : (byte)0;
        _d6 = dlc > 6 ? data[6] : (byte)0;
        _d7 = dlc > 7 ? data[7] : (byte)0;
    }

    /// <summary>Microseconds from the start of the scenario timeline.</summary>
    public uint TimestampMicroseconds => _timestampMicroseconds;

    public TimeSpan Timestamp => TimeSpan.FromTicks(_timestampMicroseconds * 10L);

    /// <summary>The identifier as recorded, without the extended marker bit.</summary>
    public uint CanId => _rawId & ~ExtendedIdFlag;

    public bool IsExtended => (_rawId & ExtendedIdFlag) != 0;

    public byte Dlc => _dlc;

    /// <summary>True for frames openpilot transmitted (panda's <c>CAN_BUS_RET_FLAG</c>).</summary>
    public bool IsTxEcho => (_flags & FlagTxEcho) != 0;

    public bool IsRemoteRequest => (_flags & FlagRemoteRequest) != 0;

    /// <summary>The payload, exactly <see cref="Dlc"/> bytes long.</summary>
    public ReadOnlySpan<byte> Data
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.AsRef(in _d0), _dlc);
    }

    public byte[] ToArray() => Data.ToArray();

    public string DataHex() => Convert.ToHexString(Data);

    public string IdText() => IsExtended ? $"0x{CanId:X8}" : $"0x{CanId:X3}";

    public bool Equals(CanFrame other) =>
        _timestampMicroseconds == other._timestampMicroseconds &&
        _rawId == other._rawId && _dlc == other._dlc && _flags == other._flags &&
        Data.SequenceEqual(other.Data);

    public override bool Equals(object? obj) => obj is CanFrame other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_timestampMicroseconds, _rawId, _dlc, _flags, _d0, _d1, _d2, _d3);

    public override string ToString() =>
        $"{_timestampMicroseconds / 1_000_000.0:F6} {IdText()} [{_dlc}] {DataHex()}" +
        (IsTxEcho ? " (tx)" : string.Empty);
}
