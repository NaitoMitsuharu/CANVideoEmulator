using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

public sealed class ConversionProgressTests
{
    [Theory]
    [InlineData("prepare", 1, 1, .05)]
    [InlineData("extract", 5000000000L, 10000000000L, .125)]
    [InlineData("build", 94, 188, .55)]
    [InlineData("verify", 191, 191, .99)]
    public void ProgressReflectsMeasuredWorkAndReservesCompletionForSuccessfulExit(string phase, long completed, long total, double fraction)
    {
        var line = $"CANVIDEO_PROGRESS {{\"phase\":\"{phase}\",\"completed\":{completed},\"total\":{total},\"detail\":\"fixture\"}}";
        Assert.True(ConversionProgressUpdate.TryParse(line, out var progress));
        Assert.Equal(fraction, progress.Fraction, 8);
        Assert.True(progress.Fraction < 1);
        Assert.Equal("fixture", progress.Detail);
    }

    [Theory]
    [InlineData("ordinary log line")]
    [InlineData("CANVIDEO_PROGRESS broken")]
    [InlineData("CANVIDEO_PROGRESS []")]
    [InlineData("CANVIDEO_PROGRESS {\"phase\":\"build\",\"completed\":1,\"total\":0}")]
    [InlineData("CANVIDEO_PROGRESS {\"phase\":\"build\",\"completed\":-1,\"total\":2}")]
    [InlineData("CANVIDEO_PROGRESS {\"phase\":\"build\",\"completed\":3,\"total\":2}")]
    [InlineData("CANVIDEO_PROGRESS {\"phase\":\"unknown\",\"completed\":1,\"total\":2}")]
    [InlineData("CANVIDEO_PROGRESS {\"phase\":\"build\",\"completed\":\"1\",\"total\":2}")]
    public void InvalidProgressCannotMoveTheBarOrCrashTheReader(string line) =>
        Assert.False(ConversionProgressUpdate.TryParse(line, out _));
}
