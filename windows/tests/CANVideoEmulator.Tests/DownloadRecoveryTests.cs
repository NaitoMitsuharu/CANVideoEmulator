using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

public class DownloadRecoveryTests
{
    [Fact]
    public async Task AFinishedPartialFileIsPromotedWithoutAnotherNetworkRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            var chunk = new DatasetChunk("sample", "sample", "http://127.0.0.1:1/unreachable",
                "sample.bin", 4, "Toyota RAV4", "");
            await File.WriteAllBytesAsync(Path.Combine(root, "sample.bin" + DatasetDownloader.PartialSuffix), [1, 2, 3, 4]);
            using var downloader = new DatasetDownloader();
            var result = await downloader.DownloadAsync(chunk, root);
            Assert.True(result.Success);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(result.Path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SampleFilesCanBeDownloadedIntoTheirNestedDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            using var client = new HttpClient(new SampleHandler());
            var chunk = new DatasetChunk("sample", "sample", "https://example.invalid/sample",
                "Example_1/route/40/global_pose/frame_times", 4, "Toyota RAV4", "");
            using var downloader = new DatasetDownloader(client);
            Assert.True((await downloader.DownloadAsync(chunk, root)).Success);
            Assert.True(DatasetDownloader.IsComplete(chunk, root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SampleHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            });
    }
}
