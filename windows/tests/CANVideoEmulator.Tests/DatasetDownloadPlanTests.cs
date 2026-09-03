using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

public sealed class DatasetDownloadPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CANVideoDownloadPlan", Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_root, "data");
    private string Legacy => Path.Combine(_root, "comma2k19");

    private static DatasetChunk FileEntry(string name, string url = "http://127.0.0.1:1/not-needed") =>
        new(name, name, url, name, 20, "Toyota RAV4", "");

    private static void Seed(string root, string name, int length)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[length]);
    }

    [Fact]
    public async Task CompletedChunksInAnotherKnownFolderRequireNoHttpRequest()
    {
        var file = FileEntry("Chunk_1.zip");
        Seed(Legacy, file.FileName, 20);
        var plan = DatasetDownloadPlan.Create([file], Target, [Legacy]);
        Assert.True(plan.IsComplete);
        Assert.Equal(0, plan.RemainingBytes);
        Assert.Equal(Legacy, plan.Items[0].Directory);
        using var downloader = new DatasetDownloader();
        // The URL is unreachable: a successful result proves no HTTP is needed.
        Assert.True((await downloader.DownloadAsync(file, plan.Items[0].Directory)).Success);
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task ACompletedSampleUsesItsExistingRootAndDoesNotFetchAnyFile()
    {
        var files = DatasetCatalog.ExampleFiles.Select(file => file with { SizeBytes = 20,
            Url = "http://127.0.0.1:1/not-needed" }).ToArray();
        foreach (var file in files) Seed(Legacy, file.FileName, 20);
        var plan = DatasetDownloadPlan.Create(files, Target, [Legacy], keepTogether: true);
        Assert.True(plan.IsComplete);
        Assert.Equal(10, plan.Items.Count);
        using var downloader = new DatasetDownloader();
        foreach (var item in plan.Items)
        {
            Assert.Equal(Legacy, item.Directory);
            Assert.True((await downloader.DownloadAsync(item.File, item.Directory)).Success);
        }
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void APartialSampleFillsTheExistingRootAndRejectsWrongSizeFiles()
    {
        var files = new[] { FileEntry("Example/video.hevc"), FileEntry("Example/raw_log.bz2"), FileEntry("Example/times") };
        Seed(Legacy, files[0].FileName, 20);
        Seed(Legacy, files[1].FileName + ".part", 7);
        Seed(Legacy, files[2].FileName, 3);
        var plan = DatasetDownloadPlan.Create(files, Target, [Legacy], keepTogether: true);
        Assert.False(plan.IsComplete);
        Assert.All(plan.Items, item => Assert.Equal(Legacy, item.Directory));
        Assert.Equal(27, plan.ExistingBytes);
        Assert.Equal(33, plan.RemainingBytes);
        Assert.True(plan.Items[0].IsComplete);
        Assert.Equal(7, plan.Items[1].ExistingBytes);
        Assert.Equal(0, plan.Items[2].ExistingBytes);
    }

    [Fact]
    public void AFileAddedAfterInitialInspectionIsFoundBeforeDownload()
    {
        var files = new[] { FileEntry("Chunk_1.zip"), FileEntry("Chunk_2.zip") };
        Assert.Equal(40, DatasetDownloadPlan.Create(files, Target, [Legacy]).RemainingBytes);
        Seed(Target, files[0].FileName, 20);
        Seed(Legacy, files[1].FileName + ".part", 10);
        var plan = DatasetDownloadPlan.Create(files, Target, [Legacy]);
        Assert.Equal(10, plan.RemainingBytes);
        Assert.True(plan.Items[0].IsComplete);
        Assert.Equal(Legacy, plan.Items[1].Directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
