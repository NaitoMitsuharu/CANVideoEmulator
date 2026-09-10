using CANVideoEmulator.Core.Can;
using CANVideoEmulator.Core.Playback;
using CANVideoEmulator.Linux;
using CANVideoEmulator.Scenarios;

var options = Options.Parse(args);
if (options is null) return 2;

var packagePath = Path.Combine(options.ScenarioDirectory, options.ScenarioId);
var package = ScenarioPackage.Load(packagePath);
var timeline = package.Timeline(options.Bus);
var clock = new PlaybackClock { Duration = package.Duration };
using var transport = new SocketCanTransport(options.InterfaceName);
await using var scheduler = new CanScheduler(clock, transport) { IncludeTxEcho = options.IncludeTxEcho };
scheduler.LoadTimeline(timeline);
scheduler.SendFailed += message => Console.Error.WriteLine($"CAN send failed: {message}");
scheduler.TimingAnomaly += message => Console.Error.WriteLine($"CAN timing: {message}");
scheduler.BusHealthChanged += status => Console.Error.WriteLine($"CAN health: {status.Health}: {status.Detail}");

using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancelled.Cancel(); };

transport.Open();
Console.WriteLine($"Sending {package.ScenarioId}, bus {options.Bus}, {timeline.Count:N0} frames to {options.InterfaceName}. Press Ctrl+C to stop.");
do
{
    scheduler.ResetStatistics();
    clock.Stop();
    scheduler.Start(TimeSpan.Zero);
    clock.Play();
    while (!clock.HasReachedEnd && !cancelled.IsCancellationRequested)
        await Task.Delay(100, cancelled.Token).ConfigureAwait(false);
    scheduler.Stop();
    var summary = scheduler.Snapshot();
    Console.WriteLine($"scheduled={summary.FramesScheduled:N0} sent={summary.FramesSent:N0} errors={summary.SendErrors:N0} p99-jitter={summary.JitterP99Ms:F2}ms");
} while (options.Loop && !cancelled.IsCancellationRequested);

return 0;

internal sealed record Options(string ScenarioDirectory, string ScenarioId, int Bus, string InterfaceName,
                               bool Loop, bool IncludeTxEcho)
{
    public static Options? Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] is "--loop" or "--exclude-tx-echo") continue;
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
                return Help();
            values[arguments[index]] = arguments[++index];
        }

        if (!values.TryGetValue("--scenario-dir", out var directory) ||
            !values.TryGetValue("--scenario", out var scenario) ||
            !values.TryGetValue("--bus", out var busText) ||
            !int.TryParse(busText, out var bus)) return Help();

        return new Options(directory, scenario, bus,
            values.GetValueOrDefault("--interface", "can0"),
            arguments.Contains("--loop", StringComparer.OrdinalIgnoreCase),
            !arguments.Contains("--exclude-tx-echo", StringComparer.OrdinalIgnoreCase));
    }

    private static Options? Help()
    {
        Console.Error.WriteLine("Usage: can-replay-linux --scenario-dir <Scenarios> --scenario <id> --bus <n> [--interface can0] [--loop] [--exclude-tx-echo]");
        return null;
    }
}