using CANVideoEmulator.Pcan;
using Peak.Can.Basic;
using Xunit;

namespace CANVideoEmulator.Tests;

public class PcanManagerTests
{
    private static readonly PcanChannelDescriptor Channel = new(PcanChannel.Usb01,
        "Usb01", "PCAN-USB", 1, 0, ChannelCondition.ChannelAvailable, default);

    [Fact]
    public void ManualRefreshReportsDisconnectOnceAndSelectsARepluggedDeviceWithoutOpeningIt()
    {
        var state = new PcanAvailability(PcanApiState.Available, "attached", null, null, [Channel]);
        using var manager = new PcanManager(TimeSpan.FromHours(1), () => state);
        var losses = 0;
        manager.ConnectionLost += (_, _) => losses++;
        Assert.Equal(Channel, manager.Selected);

        state = new(PcanApiState.NoDevices, "removed", null, null, []);
        manager.Refresh();
        manager.Refresh();
        Assert.Equal(1, losses);
        Assert.Null(manager.Selected);

        state = new(PcanApiState.Available, "replugged", null, null, [Channel]);
        manager.Refresh();
        Assert.Equal(Channel, manager.Selected);
        Assert.Null(manager.Transport);
    }
}
