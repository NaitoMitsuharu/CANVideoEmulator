using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace CANVideoEmulator.Wpf.Services;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Rolling text log for the application (requirement 65).
/// </summary>
/// <remarks>
/// <para>Deliberately small: an exhibition machine needs a file a technician can
/// open in Notepad, not a logging framework to configure. Writes go through a
/// background drain so a slow disk cannot stall the CAN scheduler's thread if it
/// ever logs.</para>
///
/// <para>Individual CAN frames are never written here (requirement 65). At around
/// 1,100 frames per second a per-frame log would produce hundreds of megabytes in
/// a single exhibition day and would itself perturb the timing being measured.
/// Frame-level detail belongs in the Scenario Builder's JSONL export.</para>
/// </remarks>
public sealed class AppLog : IDisposable
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private const int KeepFiles = 5;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly Thread _worker;
    private readonly string _directory;
    private readonly string _path;
    private bool _disposed;

    public AppLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "CANVideoEmulator.log");
        Roll();

        _worker = new Thread(Drain)
        {
            IsBackground = true,
            Name = "AppLog",
        };
        _worker.Start();
    }

    public string Path_ => _path;

    /// <summary>Raised for every entry, so the UI can surface warnings and errors.</summary>
    public event Action<LogLevel, string>? Entry;

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warning(string message) => Write(LogLevel.Warning, message);

    public void Error(string message, Exception? error = null) =>
        Write(LogLevel.Error, error is null ? message : $"{message}: {error}");

    private void Write(LogLevel level, string message)
    {
        Entry?.Invoke(level, message);

        if (_disposed)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                   $"{level.ToString().ToUpperInvariant(),-7} {message}";
        // Never block the caller: dropping a log line is preferable to stalling
        // playback.
        _queue.TryAdd(line);
    }

    private void Drain()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                if (new FileInfo(_path).Length > MaxBytes)
                {
                    Roll();
                }
            }
            catch (Exception)
            {
                // A read-only or full disk must not take the application down.
            }
        }
    }

    private void Roll()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length <= MaxBytes)
            {
                return;
            }

            var stamped = System.IO.Path.Combine(_directory,
                $"CANVideoEmulator-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(_path, stamped, overwrite: true);

            foreach (var old in new DirectoryInfo(_directory)
                         .GetFiles("CANVideoEmulator-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(KeepFiles))
            {
                old.Delete();
            }
        }
        catch (Exception)
        {
            // Rolling is best-effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
