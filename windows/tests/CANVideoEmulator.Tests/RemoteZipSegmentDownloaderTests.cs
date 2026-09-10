using System.IO.Compression;
using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

public sealed class RemoteZipSegmentDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RemoteZipTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadsIndexAndDownloadsOnlySelectedSegment()
    {
        var archive = BuildArchive();
        using var server = new RangeServer(archive);
        var chunk = new DatasetChunk("test", "Test", server.Url, "Chunk_1.zip",
            archive.Length, "Toyota RAV4", "");
        using var downloader = new RemoteZipSegmentDownloader();

        var index = await downloader.ReadIndexAsync(chunk);

        Assert.Equal(2, index.Segments.Count);
        var input = await downloader.DownloadAsync(chunk, index.Segments[1], _root);
        Assert.True(File.Exists(Path.Combine(input, "raw_log.bz2")));
        Assert.Equal("video-41", await File.ReadAllTextAsync(Path.Combine(input, "video.hevc")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Chunk_1", "route", "40")));
    }

    [Fact]
    public async Task APreviouslyExtractedSegmentDoesNotDownloadItsEntriesAgain()
    {
        var archive = BuildArchive();
        using var server = new RangeServer(archive);
        var chunk = new DatasetChunk("test", "Test", server.Url, "Chunk_1.zip",
            archive.Length, "Toyota RAV4", "");
        using var downloader = new RemoteZipSegmentDownloader();
        var segment = (await downloader.ReadIndexAsync(chunk)).Segments[0];
        await downloader.DownloadAsync(chunk, segment, _root);
        var served = Interlocked.Read(ref server.BytesServed);

        await downloader.DownloadAsync(chunk, segment, _root);

        Assert.Equal(served, Interlocked.Read(ref server.BytesServed));
    }

    private static byte[] BuildArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var segment in new[] { 40, 41 })
            {
                Add(archive, $"Chunk_1/route/{segment}/raw_log.bz2", $"raw-{segment}");
                Add(archive, $"Chunk_1/route/{segment}/video.hevc", $"video-{segment}");
                Add(archive, $"Chunk_1/route/{segment}/processed_log/IMU/gyro/t", $"imu-{segment}");
            }
            Add(archive, "Chunk_1/readme.txt", "ignored");
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.SmallestSize).Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}