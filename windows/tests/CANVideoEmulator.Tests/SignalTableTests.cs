using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

public class SignalTableTests
{
    private static string WriteSignals(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"signals_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static SignalTable Load()
    {
        var path = WriteSignals("""
        {
          "bus": 0,
          "profile": "test_profile",
          "messages": [
            {
              "can_id": 100,
              "name": "TEST_LE",
              "signals": [
                { "name": "VALUE_LE", "start_bit": 0, "length": 8,
                  "byte_order": "little_endian", "signed": false,
                  "factor": 1.0, "offset": 0.0, "unit": "" }
              ]
            },
            {
              "can_id": 200,
              "name": "TEST_BE",
              "signals": [
                { "name": "VALUE_BE", "start_bit": 7, "length": 8,
                  "byte_order": "big_endian", "signed": false,
                  "factor": 1.0, "offset": 0.0, "unit": "" },
                { "name": "VALUE_SIGNED", "start_bit": 7, "length": 8,
                  "byte_order": "big_endian", "signed": true,
                  "factor": 1.0, "offset": 0.0, "unit": "" },
                { "name": "VALUE_NIBBLE", "start_bit": 12, "length": 4,
                  "byte_order": "big_endian", "signed": false,
                  "factor": 1.0, "offset": 0.0, "unit": "" },
                { "name": "VALUE_SCALED", "start_bit": 7, "length": 8,
                  "byte_order": "big_endian", "signed": false,
                  "factor": 0.5, "offset": -10.0, "unit": "km/h" }
              ]
            }
          ]
        }
        """);
        try
        {
            var table = SignalTable.TryLoad(path);
            Assert.NotNull(table);
            return table!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LittleEndianByteAlignedSignalReadsTheWholeByte()
    {
        var table = Load();
        Assert.True(table.TryGetSignal("TEST_LE", "VALUE_LE", out _, out var signal));
        var value = SignalTable.Decode([0xAB, 0x00], signal);
        Assert.Equal(0xAB, value);
    }

    [Fact]
    public void BigEndianByteAlignedSignalMatchesTheSameByte()
    {
        // start_bit 7, length 8 is the standard Motorola "whole first byte"
        // encoding -- it must decode identically to the little-endian case above
        // for the same input, since both describe byte 0 in full.
        var table = Load();
        Assert.True(table.TryGetSignal("TEST_BE", "VALUE_BE", out _, out var signal));
        var value = SignalTable.Decode([0xAB, 0x00], signal);
        Assert.Equal(0xAB, value);
    }

    [Fact]
    public void SignedBigEndianTopBitProducesNegativeValue()
    {
        var table = Load();
        Assert.True(table.TryGetSignal("TEST_BE", "VALUE_SIGNED", out _, out var signal));
        var value = SignalTable.Decode([0xFF, 0x00], signal);
        Assert.Equal(-1, value);
    }

    [Fact]
    public void BigEndianNibbleInsideOneByteExtractsTheMiddleBits()
    {
        // byte1 = 0x3C = 0b0011_1100; bits 1..4 (LSB-first) = 0b1110 = 14.
        var table = Load();
        Assert.True(table.TryGetSignal("TEST_BE", "VALUE_NIBBLE", out _, out var signal));
        var value = SignalTable.Decode([0x00, 0x3C], signal);
        Assert.Equal(14, value);
    }

    [Fact]
    public void FactorAndOffsetAreAppliedAfterExtraction()
    {
        var table = Load();
        Assert.True(table.TryGetSignal("TEST_BE", "VALUE_SCALED", out _, out var signal));
        var value = SignalTable.Decode([0x64, 0x00], signal); // 0x64 = 100
        Assert.Equal(100 * 0.5 - 10.0, value);
    }

    [Fact]
    public void DecodeReturnsNullWhenTheFrameIsShorterThanTheSignalNeeds()
    {
        var table = Load();
        Assert.True(table.TryGetSignal("TEST_LE", "VALUE_LE", out _, out var signal));
        Assert.Null(SignalTable.Decode([], signal));
    }

    [Fact]
    public void FindByCanIdAndTryGetSignalAgreeOnTheSameMessage()
    {
        var table = Load();
        var message = table.FindByCanId(200);
        Assert.NotNull(message);
        Assert.Equal("TEST_BE", message!.Name);
        Assert.False(table.TryGetSignal("TEST_BE", "NOT_A_SIGNAL", out _, out _));
        Assert.False(table.TryGetSignal("NOT_A_MESSAGE", "VALUE_BE", out _, out _));
    }
}
