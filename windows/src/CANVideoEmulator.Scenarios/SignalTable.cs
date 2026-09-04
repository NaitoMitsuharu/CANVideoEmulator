using System.Text.Json;
using System.Text.Json.Serialization;

namespace CANVideoEmulator.Scenarios;

/// <summary>
/// One DBC signal's bit layout, as precomputed into the package's
/// <c>dbc/signals.json</c> by the builder (see docs/scenario_package.md).
/// </summary>
public sealed record SignalDefinition(string Name, int StartBit, int Length,
                                       bool BigEndian, bool Signed,
                                       double Factor, double Offset, string Unit);

/// <summary>One CAN message and the signals packed into it.</summary>
public sealed record MessageDefinition(uint CanId, string Name,
                                       IReadOnlyList<SignalDefinition> Signals);

/// <summary>
/// The scenario's matched DBC profile, reduced to a flat bit-layout table so the
/// player can decode a handful of named signals live without parsing DBC syntax.
/// </summary>
/// <remarks>
/// Mirrors <c>scenario_builder/scenario_builder/dbc.py</c>'s
/// <c>extract_signal</c> bit numbering exactly: bit <c>n</c> of the frame is bit
/// <c>n % 8</c> of byte <c>n // 8</c>. Cosmetic only -- a missing or malformed
/// signals.json yields no table rather than an error.
/// </remarks>
public sealed class SignalTable
{
    private readonly Dictionary<uint, MessageDefinition> _byCanId;
    private readonly Dictionary<string, MessageDefinition> _byName;

    private SignalTable(int bus, string profile,
                        Dictionary<uint, MessageDefinition> byCanId,
                        Dictionary<string, MessageDefinition> byName)
    {
        Bus = bus;
        Profile = profile;
        _byCanId = byCanId;
        _byName = byName;
    }

    public int Bus { get; }
    public string Profile { get; }

    public MessageDefinition? FindByCanId(uint canId) => _byCanId.GetValueOrDefault(canId);

    /// <summary>Look up one signal by its message and signal name.</summary>
    public bool TryGetSignal(string messageName, string signalName,
                             out MessageDefinition message, out SignalDefinition signal)
    {
        if (_byName.TryGetValue(messageName, out var found))
        {
            var match = found.Signals.FirstOrDefault(s => s.Name == signalName);
            if (match is not null)
            {
                message = found;
                signal = match;
                return true;
            }
        }

        message = null!;
        signal = null!;
        return false;
    }

    public static SignalTable? TryLoad(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<SignalsDocument>(stream);
            if (document is null)
            {
                return null;
            }

            var byCanId = new Dictionary<uint, MessageDefinition>();
            var byName = new Dictionary<string, MessageDefinition>(StringComparer.Ordinal);
            foreach (var message in document.Messages)
            {
                var signals = message.Signals
                    .Select(s => new SignalDefinition(
                        s.Name, s.StartBit, s.Length,
                        string.Equals(s.ByteOrder, "big_endian", StringComparison.Ordinal),
                        s.Signed, s.Factor, s.Offset, s.Unit ?? string.Empty))
                    .ToArray();
                var definition = new MessageDefinition((uint)message.CanId, message.Name, signals);
                byCanId[definition.CanId] = definition;
                byName[definition.Name] = definition;
            }

            return new SignalTable(document.Bus, document.Profile ?? string.Empty, byCanId, byName);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decode one signal from a frame's payload, or null when the frame is too
    /// short for it (a mismatched or malformed capture, not a normal condition).
    /// </summary>
    public static double? Decode(ReadOnlySpan<byte> data, SignalDefinition signal)
    {
        if (signal.Length is <= 0 or > 64)
        {
            return null;
        }

        Span<int> bitPositions = stackalloc int[signal.Length];
        if (!signal.BigEndian)
        {
            for (var i = 0; i < signal.Length; i++)
            {
                bitPositions[i] = signal.StartBit + i;
            }
        }
        else
        {
            // Motorola: walk forward through the bytes from the start bit,
            // collecting MSB-first, then reverse so index 0 is the LSB (weight 0).
            var byteIndex = signal.StartBit / 8;
            var bitIndex = signal.StartBit % 8;
            for (var i = 0; i < signal.Length; i++)
            {
                bitPositions[signal.Length - 1 - i] = byteIndex * 8 + bitIndex;
                if (bitIndex == 0)
                {
                    byteIndex++;
                    bitIndex = 7;
                }
                else
                {
                    bitIndex--;
                }
            }
        }

        long value = 0;
        for (var weight = 0; weight < signal.Length; weight++)
        {
            var (byteIndex, bitIndex) = Math.DivRem(bitPositions[weight], 8);
            if (byteIndex >= data.Length)
            {
                return null;
            }

            if (((data[byteIndex] >> bitIndex) & 1) != 0)
            {
                value |= 1L << weight;
            }
        }

        if (signal.Signed && ((value >> (signal.Length - 1)) & 1) != 0)
        {
            value -= 1L << signal.Length;
        }

        return value * signal.Factor + signal.Offset;
    }

    private sealed class SignalsDocument
    {
        [JsonPropertyName("bus")] public int Bus { get; set; }
        [JsonPropertyName("profile")] public string? Profile { get; set; }
        [JsonPropertyName("messages")] public List<MessageJson> Messages { get; set; } = [];
    }

    private sealed class MessageJson
    {
        [JsonPropertyName("can_id")] public int CanId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("signals")] public List<SignalJson> Signals { get; set; } = [];
    }

    private sealed class SignalJson
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("start_bit")] public int StartBit { get; set; }
        [JsonPropertyName("length")] public int Length { get; set; }
        [JsonPropertyName("byte_order")] public string ByteOrder { get; set; } = string.Empty;
        [JsonPropertyName("signed")] public bool Signed { get; set; }
        [JsonPropertyName("factor")] public double Factor { get; set; } = 1.0;
        [JsonPropertyName("offset")] public double Offset { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }
    }
}
