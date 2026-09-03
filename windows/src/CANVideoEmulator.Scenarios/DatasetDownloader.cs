using System.Diagnostics;

namespace CANVideoEmulator.Scenarios;

/// <summary>
/// Downloads a dataset chunk over HTTPS, resuming where it left off.
/// </summary>
/// <remarks>
/// <para>These files are around 9 GB, so resume is not a nicety: on a booth
/// network a download will be interrupted, and restarting from zero each time
/// would make the feature useless. The mirror advertises
/// <c>Accept-Ranges: bytes</c>, so an interrupted transfer continues with a
/// <c>Range</c> request against the bytes already on disk.</para>
///
/// <para>The download lands in a <c>.part</c> file and is only renamed once the
/// length matches what the catalogue expects. Nothing downstream ever sees a
/// half-written archive.</para>
/// </remarks>
public sealed class DatasetDownloader : IDisposable
{
    /// <summary>Suffix used while a transfer is in flight.</summary>
    public const string PartialSuffix = ".part";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public DatasetDownloader(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            // The content is many gigabytes; the per-request timeout has to cover
            // the whole body, and cancellation is what stops a stalled transfer.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Bytes already fetched for <paramref name="chunk"/>, if any.</summary>
    public static long ExistingBytes(DatasetChunk chunk, string directory)
    {
        var complete = Path.Combine(directory, chunk.FileName);
        if (File.Exists(complete))
        {
            return new FileInfo(complete).Length;
        }

        var partial = complete + PartialSuffix;
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    public static bool IsComplete(DatasetChunk chunk, string directory)
    {
        var path = Path.Combine(directory, chunk.FileName);
        return File.Exists(path) && new FileInfo(path).Length == chunk.SizeBytes;
    }

    /// <summary>
    /// Fetch <paramref name="chunk"/> into <paramref name="directory"/>, resuming
    /// an interrupted transfer.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        DatasetChunk chunk,
        string directory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, chunk.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var partialPath = finalPath + PartialSuffix;

        if (IsComplete(chunk, directory))
        {
            return new DownloadResult(true, finalPath, chunk.SizeBytes, "Already downloaded.");
        }

        // A file of the right name but the wrong size is a previous failure, not
        // a usable archive; start it again rather than resuming into it.
        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        var resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (resumeFrom > chunk.SizeBytes)
        {
            File.Delete(partialPath);
            resumeFrom = 0;
        }

        if (resumeFrom == chunk.SizeBytes && resumeFrom > 0)
        {
            File.Move(partialPath, finalPath, overwrite: true);
            return new DownloadResult(true, finalPath, resumeFrom, "Recovered completed download.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, chunk.Url);
            if (resumeFrom > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
            }

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // A transient HTTP error must not discard the resumable partial file.
            response.EnsureSuccessStatusCode();

            // A server that ignores the Range header answers 200 with the whole
            // body; appending that to what is already there would corrupt the file.
            if (resumeFrom > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                resumeFrom = 0;
                File.Delete(partialPath);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                (response.Content.Headers.ContentRange?.From != resumeFrom ||
                 response.Content.Headers.ContentRange?.Length != chunk.SizeBytes))
            {
                throw new HttpRequestException("Server returned an unexpected Content-Range; partial data was preserved.");
            }

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             partialPath,
                             resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                             FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await CopyAsync(source, destination, chunk, resumeFrom, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The partial file is deliberately kept so the next attempt resumes.
            var kept = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            return new DownloadResult(false, partialPath, kept,
                $"Cancelled. {kept / 1_000_000_000.0:0.00} GB kept; the next attempt resumes.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            var kept = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            return new DownloadResult(false, partialPath, kept,
                $"Download failed: {error.Message}. " +
                $"{kept / 1_000_000_000.0:0.00} GB kept; retrying resumes.");
        }

        var downloaded = new FileInfo(partialPath).Length;
        if (downloaded != chunk.SizeBytes)
        {
            return new DownloadResult(false, partialPath, downloaded,
                $"Download finished at {downloaded:N0} bytes but {chunk.SizeBytes:N0} were " +
                "expected. The file is incomplete; try again to resume.");
        }

        File.Move(partialPath, finalPath, overwrite: true);
        return new DownloadResult(true, finalPath, downloaded, "Download complete.");
    }

    private static async Task CopyAsync(Stream source, Stream destination, DatasetChunk chunk,
                                        long alreadyHave, IProgress<DownloadProgress>? progress,
                                        CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 20];
        var total = alreadyHave;
        var sinceReport = 0L;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var lastReportedTotal = alreadyHave;

        progress?.Report(new DownloadProgress(total, chunk.SizeBytes, 0));

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            total += read;
            sinceReport += read;

            // Report a few times a second at most: a progress event per 1 MB
            // block would be several hundred UI updates a second on a fast link.
            var elapsed = clock.Elapsed;
            if (elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
            {
                var seconds = (elapsed - lastReport).TotalSeconds;
                var bytesPerSecond = seconds > 0 ? (total - lastReportedTotal) / seconds : 0;
                progress?.Report(new DownloadProgress(total, chunk.SizeBytes, bytesPerSecond));
                lastReport = elapsed;
                lastReportedTotal = total;
                sinceReport = 0;
            }
        }

        _ = sinceReport;
        progress?.Report(new DownloadProgress(total, chunk.SizeBytes, 0));
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}

public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes,
                                               double BytesPerSecond)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);

    public string ReceivedText => $"{BytesReceived / 1_000_000_000.0:0.00} GB";

    public string TotalText => $"{TotalBytes / 1_000_000_000.0:0.00} GB";

    public string SpeedText => BytesPerSecond <= 0
        ? "--"
        : $"{BytesPerSecond / 1_000_000.0:0.0} MB/s";

    public TimeSpan? Remaining => BytesPerSecond <= 0
        ? null
        : TimeSpan.FromSeconds((TotalBytes - BytesReceived) / BytesPerSecond);
}

public readonly record struct DownloadResult(bool Success, string Path, long BytesOnDisk,
                                             string Message);
