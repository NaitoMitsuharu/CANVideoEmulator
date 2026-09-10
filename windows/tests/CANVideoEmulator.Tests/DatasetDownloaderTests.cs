using System.Net;
using System.Net.Http;
using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

/// <summary>
/// A minimal HTTP server that serves one byte array and honours Range requests,
/// so the resume path can be exercised without touching the network.
/// </summary>
internal sealed class RangeServer : IDisposable
{
    private HttpListener _listener = new();
    private readonly byte[] _payload;
    private readonly bool _supportsRange;
    private readonly CancellationTokenSource _stop = new();

    public RangeServer(byte[] payload, bool supportsRange = true, int port = 0)
    {
        _payload = payload;
        _supportsRange = supportsRange;

        // HttpListener needs a fixed port; probe upwards for a free one.
        var chosen = port == 0 ? 18_800 : port;
        while (true)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{chosen}/");
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (chosen < 18_900)
            {
                _listener.Close();
                _listener = new HttpListener();
                chosen++;
            }
        }

        Url = $"http://127.0.0.1:{chosen}/chunk.zip";
        _ = Task.Run(ServeAsync);
    }

    public string Url { get; }

    /// <summary>Bytes served so far, so a test can prove a resume was partial.</summary>
    public long BytesServed;

    /// <summary>Drop the connection after this many bytes, to simulate a failure.</summary>
    public long? BreakAfterBytes { get; set; }

    /// <summary>Pause between slices, so a transfer lasts long enough to cancel.</summary>
    public TimeSpan DelayPerSlice { get; set; } = TimeSpan.Zero;

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                var from = 0L;
                var to = _payload.LongLength - 1;
                var rangeHeader = context.Request.Headers["Range"];
                if (_supportsRange && !string.IsNullOrEmpty(rangeHeader) &&
                    rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = rangeHeader["bytes=".Length..].Split('-');
                    long.TryParse(spec[0], out from);
                    if (spec.Length > 1 && long.TryParse(spec[1], out var requestedTo))
                        to = Math.Min(requestedTo, to);
                    context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                    context.Response.Headers["Content-Range"] =
                        $"bytes {from}-{to}/{_payload.Length}";
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                }

                var slice = _payload.AsMemory((int)from, checked((int)(to - from + 1)));
                var limit = BreakAfterBytes is { } cap
                    ? (int)Math.Min(cap, slice.Length)
                    : slice.Length;

                context.Response.ContentLength64 = slice.Length;

                // Written in slices so a test can throttle the transfer and act
                // on it while it is still in flight.
                const int Slice = 32 * 1024;
                for (var offset = 0; offset < limit; offset += Slice)
                {
                    var length = Math.Min(Slice, limit - offset);
                    await context.Response.OutputStream
                        .WriteAsync(slice.Slice(offset, length));
                    Interlocked.Add(ref BytesServed, length);
                    if (DelayPerSlice > TimeSpan.Zero)
                    {
                        await Task.Delay(DelayPerSlice);
                    }
                }

                if (BreakAfterBytes is not null)
                {
                    // Abort mid-body so the client sees a truncated transfer.
                    context.Response.Abort();
                    continue;
                }

                context.Response.Close();
            }
            catch (Exception)
            {
                try { context.Response.Abort(); } catch { /* already gone */ }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
        _stop.Dispose();
    }
}

public class DatasetCatalogTests
{
    [Fact]
    public void AllRav4AndCivicChunksAreOffered()
    {
        Assert.Equal(10, DatasetCatalog.Chunks.Count);
        Assert.All(DatasetCatalog.Chunks.Take(2), c => Assert.Equal("Toyota RAV4", c.Vehicle));
        Assert.All(DatasetCatalog.Chunks.Skip(2), c => Assert.Equal("Honda Civic", c.Vehicle));
        Assert.Equal(9_054_474_112, DatasetCatalog.Find("chunk_2")!.SizeBytes);
        Assert.Equal(9_901_342_289, DatasetCatalog.Find("chunk_10")!.SizeBytes);
    }

    [Fact]
    public void SizesMatchThePublishedDataset()
    {
        // Verified against the mirror's Content-Length and the torrent listing.
        Assert.Equal(8_731_252_405, DatasetCatalog.Find("chunk_1")!.SizeBytes);
        Assert.Equal("8.73 GB", DatasetCatalog.Find("chunk_1")!.SizeText);
        Assert.Equal("9.05 GB", DatasetCatalog.Find("chunk_2")!.SizeText);
    }

    [Fact]
    public void UrlsPointAtCommaAisOwnMirror()
    {
        Assert.All(DatasetCatalog.Chunks, c =>
        {
            Assert.StartsWith("https://huggingface.co/datasets/commaai/comma2k19/", c.Url);
            Assert.EndsWith(".zip", c.Url);
        });
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndMissesReturnNull()
    {
        Assert.NotNull(DatasetCatalog.Find("CHUNK_1"));
        Assert.NotNull(DatasetCatalog.Find("chunk_7"));
        Assert.Null(DatasetCatalog.Find("chunk_11"));
    }
}

public class DatasetDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "CanReplayDownloadTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a held file must not mask a real failure */ }
    }

    private static DatasetChunk Chunk(string url, long size) =>
        new("test", "Test chunk", url, "chunk.zip", size, "Toyota RAV4", "test");

    private static byte[] Payload(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task DownloadsAWholeFile()
    {
        var payload = Payload(200_000);
        using var server = new RangeServer(payload);
        var downloader = new DatasetDownloader();

        var result = await downloader.DownloadAsync(
            Chunk(server.Url, payload.Length), _directory);

        Assert.True(result.Success, result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
        Assert.EndsWith("chunk.zip", result.Path);
    }

    [Fact]
    public async Task ReportsProgressThatReachesTheEnd()
    {
        var payload = Payload(400_000);
        using var server = new RangeServer(payload);
        var downloader = new DatasetDownloader();

        var reports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p =>
        {
            lock (reports) { reports.Add(p); }
        });

        var result = await downloader.DownloadAsync(
            Chunk(server.Url, payload.Length), _directory, progress);

        Assert.True(result.Success);
        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.Equal(payload.Length, reports[^1].BytesReceived);
            Assert.Equal(1.0, reports[^1].Fraction);
        }
    }

    [Fact]
    public async Task AnInterruptedDownloadKeepsWhatItGotAndResumes()
    {
        var payload = Payload(300_000);
        using var server = new RangeServer(payload) { BreakAfterBytes = 100_000 };
        var chunk = Chunk(server.Url, payload.Length);
        var downloader = new DatasetDownloader();

        var first = await downloader.DownloadAsync(chunk, _directory);
        Assert.False(first.Success);
        Assert.True(first.BytesOnDisk > 0, "the partial file must be kept for the resume");
        Assert.True(first.BytesOnDisk < payload.Length);

        // Let the server serve the rest, then resume.
        server.BreakAfterBytes = null;
        var partialBefore = DatasetDownloader.ExistingBytes(chunk, _directory);
        var servedBefore = Interlocked.Read(ref server.BytesServed);

        var second = await downloader.DownloadAsync(chunk, _directory);

        Assert.True(second.Success, second.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(second.Path));

        // The resume must fetch only the remainder, not the whole file again.
        var servedBySecond = Interlocked.Read(ref server.BytesServed) - servedBefore;
        Assert.Equal(payload.Length - partialBefore, servedBySecond);
    }

    [Fact]
    public async Task AServerThatIgnoresRangeRestartsRatherThanCorruptingTheFile()
    {
        var payload = Payload(200_000);
        // Pre-seed a partial file, then serve from a server with no Range support.
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(
            Path.Combine(_directory, "chunk.zip" + DatasetDownloader.PartialSuffix),
            payload.Take(50_000).ToArray());

        using var server = new RangeServer(payload, supportsRange: false);
        var result = await new DatasetDownloader()
            .DownloadAsync(Chunk(server.Url, payload.Length), _directory);

        Assert.True(result.Success, result.Message);
        // Appending a full body onto the existing 50 kB would have produced a
        // 250 kB file; the content must be exactly the payload.
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
    }

    [Fact]
    public async Task AFinishedDownloadOfTheWrongLengthIsRejected()
    {
        var payload = Payload(100_000);
        using var server = new RangeServer(payload);

        // Claim a larger size than the server will ever produce.
        var result = await new DatasetDownloader()
            .DownloadAsync(Chunk(server.Url, payload.Length + 5_000), _directory);

        Assert.False(result.Success);
        Assert.Contains("expected", result.Message);
        // It stays as a .part, so nothing downstream sees a truncated archive.
        Assert.False(File.Exists(Path.Combine(_directory, "chunk.zip")));
    }

    [Fact]
    public async Task AnAlreadyCompleteFileIsNotFetchedAgain()
    {
        var payload = Payload(50_000);
        using var server = new RangeServer(payload);
        var chunk = Chunk(server.Url, payload.Length);
        var downloader = new DatasetDownloader();

        Assert.True((await downloader.DownloadAsync(chunk, _directory)).Success);
        var servedAfterFirst = Interlocked.Read(ref server.BytesServed);

        var second = await downloader.DownloadAsync(chunk, _directory);

        Assert.True(second.Success);
        Assert.Contains("Already downloaded", second.Message);
        Assert.Equal(servedAfterFirst, Interlocked.Read(ref server.BytesServed));
    }

    [Fact]
    public async Task CancellationKeepsThePartialFile()
    {
        var payload = Payload(2_000_000);
        // Throttled so the transfer is still in flight when the token trips;
        // an unthrottled 2 MB finishes before any cancellation can land.
        using var server = new RangeServer(payload)
        {
            DelayPerSlice = TimeSpan.FromMilliseconds(40),
        };
        var chunk = Chunk(server.Url, payload.Length);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var result = await new DatasetDownloader()
            .DownloadAsync(chunk, _directory, cancellationToken: cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("resumes", result.Message);
        Assert.False(DatasetDownloader.IsComplete(chunk, _directory));
    }

    [Fact]
    public void ExistingBytesReportsNothingForAnUntouchedDirectory()
    {
        var chunk = Chunk("http://127.0.0.1:1/none.zip", 100);
        Assert.Equal(0, DatasetDownloader.ExistingBytes(chunk, _directory));
        Assert.False(DatasetDownloader.IsComplete(chunk, _directory));
    }

    [Fact]
    public void ProgressFormatsForDisplay()
    {
        var progress = new DownloadProgress(4_365_626_202, 8_731_252_405, 12_500_000);
        Assert.Equal("4.37 GB", progress.ReceivedText);
        Assert.Equal("8.73 GB", progress.TotalText);
        Assert.Equal("12.5 MB/s", progress.SpeedText);
        Assert.Equal(0.5, progress.Fraction, 2);
        Assert.NotNull(progress.Remaining);

        var stalled = new DownloadProgress(0, 100, 0);
        Assert.Equal("--", stalled.SpeedText);
        Assert.Null(stalled.Remaining);
    }
}
