namespace CANVideoEmulator.Scenarios;

/// <summary>Inspects known download folders before any network request is made.</summary>
public sealed record DatasetDownloadPlan(IReadOnlyList<DatasetDownloadItem> Items)
{
    public bool IsComplete => Items.Count > 0 && Items.All(item => item.IsComplete);
    public long ExistingBytes => Items.Sum(item => item.ExistingBytes);
    public long TotalBytes => Items.Sum(item => item.File.SizeBytes);
    public long RemainingBytes => TotalBytes - ExistingBytes;

    public static DatasetDownloadPlan Create(IReadOnlyList<DatasetChunk> files,
        string targetDirectory, IEnumerable<string> knownDirectories, bool keepTogether = false)
    {
        var directories = new[] { targetDirectory }.Concat(knownDirectories)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // A sample's ten files form one builder input directory. Reuse the most
        // complete existing copy and fill its missing files, keeping it usable.
        if (keepTogether)
            directories = [directories.OrderByDescending(directory =>
                files.Sum(file => Inspect(file, directory).ExistingBytes)).First()];

        return new DatasetDownloadPlan(files.Select(file => directories
            .Select(directory => Inspect(file, directory))
            .OrderByDescending(item => item.IsComplete)
            .ThenByDescending(item => item.ExistingBytes).First()).ToArray());
    }

    private static DatasetDownloadItem Inspect(DatasetChunk file, string directory)
    {
        try
        {
            if (DatasetDownloader.IsComplete(file, directory))
                return new(file, directory, true, file.SizeBytes);
            var partial = new FileInfo(Path.Combine(directory, file.FileName) + DatasetDownloader.PartialSuffix);
            // Wrong-size final files and oversized partial files are not cached
            // progress. The downloader will replace those failed transfers.
            if (partial.Exists && partial.Length <= file.SizeBytes)
                return new(file, directory, false, partial.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new(file, directory, false, 0);
    }
}

public sealed record DatasetDownloadItem(DatasetChunk File, string Directory, bool IsComplete, long ExistingBytes)
{
    public string StatusText => IsComplete ? "Downloaded — no download needed"
        : ExistingBytes > 0 ? $"Resume · {ExistingBytes / 1e6:0.0} / {File.SizeBytes / 1e6:0.0} MB"
        : "Not downloaded";
}
