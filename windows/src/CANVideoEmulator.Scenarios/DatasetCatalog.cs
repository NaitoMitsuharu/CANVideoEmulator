namespace CANVideoEmulator.Scenarios;

/// <summary>
/// What can be downloaded, and from where.
/// </summary>
/// <remarks>Official mirror sizes checked on 2026-09-03. Chunks 1–2 are RAV4;
/// chunks 3–10 are Civic. Both replay raw CAN without requiring a receiver decoder.</remarks>
public static class DatasetCatalog
{
    public const string ExampleDirectory = "Example_1/b0c9d2329ad1606b_2018-08-02--08-34-47/40";

    public static IReadOnlyList<DatasetChunk> ExampleFiles { get; } = LoadExample();

    private static IReadOnlyList<DatasetChunk> LoadExample()
    {
        using var stream = typeof(DatasetCatalog).Assembly.GetManifestResourceStream("comma2k19-example.json")!;
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        return json.RootElement.EnumerateArray().Select(file => new DatasetChunk(
            file.GetProperty("path").GetString()!, "Official one-minute example",
            file.GetProperty("url").GetString()!, file.GetProperty("path").GetString()!,
            file.GetProperty("size").GetInt64(), "Toyota RAV4", "Video, CAN, timestamps and validation references")).ToArray();
    }

    public const string DatasetName = "comma2k19";

    public const string DatasetPage = "https://huggingface.co/datasets/commaai/comma2k19";

    public const string TorrentPage =
        "https://academictorrents.com/details/65a2fbc964078aff62076ff4e103f18b951c5ddb";

    public static IReadOnlyList<DatasetChunk> Chunks { get; } =
        new long[] { 8731252405, 9054474112, 9412088454, 9490564706, 9812054419, 9526597274, 9286503359, 9634728776, 9773161870, 9901342289 }
        .Select((size, index) => new DatasetChunk(
            $"chunk_{index + 1}", $"Chunk {index + 1} — {(index < 2 ? "Toyota RAV4" : "Honda Civic")}",
            $"https://huggingface.co/datasets/commaai/comma2k19/resolve/main/raw_data/Chunk_{index + 1}.zip",
            $"Chunk_{index + 1}.zip", size, index < 2 ? "Toyota RAV4" : "Honda Civic",
            "Recorded video and CAN. Converted automatically after download.")).ToArray();

    public static DatasetChunk? Find(string id) =>
        Chunks.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <param name="SizeBytes">
/// Expected size. A completed download whose length does not match this is
/// rejected: a truncated 8 GB zip that only fails later, during extraction, is a
/// much worse experience than one that fails immediately.
/// </param>
public sealed record DatasetChunk(
    string Id,
    string Title,
    string Url,
    string FileName,
    long SizeBytes,
    string Vehicle,
    string Description)
{
    public string SizeText => $"{SizeBytes / 1_000_000_000.0:0.00} GB";
}
