namespace CanReplayPlayer.Scenarios;

/// <summary>
/// What can be downloaded, and from where.
/// </summary>
/// <remarks>
/// <para>comma2k19 is published as a torrent, which an exhibition PC generally
/// cannot use. comma.ai also mirror the same chunks on Hugging Face over plain
/// HTTPS, ungated and resumable, which is what these entries point at. The sizes
/// below are the mirror's own <c>Content-Length</c>, and they match the torrent's
/// listing.</para>
///
/// <para>Only the RAV4 chunks are offered. comma2k19's README states chunks 1-2
/// are the Toyota RAV4 and 3-10 a Honda Civic; the Scenario Builder ships a RAV4
/// DBC profile and nothing for the Civic, so downloading a Civic chunk would
/// produce packages the player could not decode.</para>
/// </remarks>
public static class DatasetCatalog
{
    public const string DatasetName = "comma2k19";

    public const string DatasetPage = "https://huggingface.co/datasets/commaai/comma2k19";

    public const string TorrentPage =
        "https://academictorrents.com/details/65a2fbc964078aff62076ff4e103f18b951c5ddb";

    public static IReadOnlyList<DatasetChunk> Chunks { get; } =
    [
        new DatasetChunk(
            Id: "chunk_1",
            Title: "Chunk 1 — Toyota RAV4",
            Url: "https://huggingface.co/datasets/commaai/comma2k19/resolve/main/raw_data/Chunk_1.zip",
            FileName: "Chunk_1.zip",
            SizeBytes: 8_731_252_405,
            Vehicle: "Toyota RAV4",
            Description: "About 200 one-minute segments. Enough for an exhibition on its own."),

        new DatasetChunk(
            Id: "chunk_2",
            Title: "Chunk 2 — Toyota RAV4",
            Url: "https://huggingface.co/datasets/commaai/comma2k19/resolve/main/raw_data/Chunk_2.zip",
            FileName: "Chunk_2.zip",
            SizeBytes: 9_045_265_780,
            Vehicle: "Toyota RAV4",
            Description: "The second RAV4 chunk. Only needed for more variety than Chunk 1 offers."),
    ];

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
