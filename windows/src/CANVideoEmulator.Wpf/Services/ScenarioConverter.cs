using System.Diagnostics;
using System.IO;
using System.Text;
using CANVideoEmulator.Scenarios;

namespace CANVideoEmulator.Wpf.Services;

/// <summary>Runs the clone's builder without opening a terminal window.</summary>
public static class ScenarioConverter
{
    public static string? FindScript(string scenarioDirectory)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, scenarioDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var script = Path.Combine(directory.FullName, "scripts", "prepare_scenarios.ps1");
                if (File.Exists(script)) return script;
            }
        return null;
    }

    public static async Task ConvertAsync(string script, string input, string output,
        IProgress<string> progress, CancellationToken cancellationToken,
        IProgress<ConversionProgressUpdate>? measuredProgress = null, bool checkOnly = false)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Directory.GetParent(Path.GetDirectoryName(script)!)!.FullName,
        };
        var arguments = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };
        if (checkOnly) arguments.Add("-CheckOnly");
        else arguments.AddRange(["-InputPath", input, "-OutputPath", output, "-Count", "0"]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (ConversionProgressUpdate.TryParse(line, out var update)) measuredProgress?.Report(update);
                else progress.Report(line);
            }
        }
        var readers = Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError));
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await readers;
            if (process.ExitCode != 0) throw new InvalidOperationException(
                $"Conversion exited with code {process.ExitCode}. See the conversion log; retry reuses completed scenarios.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await readers;
            throw;
        }
    }
}
