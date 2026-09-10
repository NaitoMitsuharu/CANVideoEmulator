using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace CANVideoEmulator.Scenarios;

/// <summary>Downloads individual comma2k19 segments from a remote chunk ZIP.</summary>
public sealed class RemoteZipSegmentDownloader : IDisposable
{
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public RemoteZipSegmentDownloader(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<RemoteZipIndex> ReadIndexAsync(DatasetChunk chunk,
        CancellationToken cancellationToken = default)
    {
        var tailLength = Math.Min(chunk.SizeBytes, 128 * 1024);
        var tailOffset = chunk.SizeBytes - tailLength;
        var tail = await ReadRangeAsync(chunk.Url, tailOffset, chunk.SizeBytes - 1, cancellationToken);
        var end = FindSignature(tail, EndSignature);
        if (end < 0) throw new InvalidDataException("The remote file has no ZIP end record.");

        long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10, 2));
        long centralSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12, 4));
        long centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16, 4));
        if (entryCount == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            var locatorOffset = tailOffset + end - 20;
            var locator = await ReadRangeAsync(chunk.Url, locatorOffset, locatorOffset + 19, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
                throw new InvalidDataException("The ZIP64 locator is missing.");
            var zip64Offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, 8)));
            var zip64 = await ReadRangeAsync(chunk.Url, zip64Offset, zip64Offset + 55, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EndSignature)
                throw new InvalidDataException("The ZIP64 end record is missing.");
            entryCount = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32, 8)));
            centralSize = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40, 8)));
            centralOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48, 8)));
        }

        if (centralSize <= 0 || centralSize > 256 * 1024 * 1024)
            throw new InvalidDataException($"Unexpected ZIP index size: {centralSize:N0} bytes.");
        var central = await ReadRangeAsync(chunk.Url, centralOffset,
            centralOffset + centralSize - 1, cancellationToken);
        var entries = ParseCentralDirectory(central, entryCount);
        var groups = entries
            .Where(entry => IsBuilderInput(entry.Name))
            .GroupBy(entry => SegmentPrefix(entry.Name))
            .Where(group => group.Key is not null &&
                group.Any(entry => entry.Name.EndsWith("/raw_log.bz2", StringComparison.OrdinalIgnoreCase)) &&
                group.Any(entry => entry.Name.EndsWith("/video.hevc", StringComparison.OrdinalIgnoreCase)))
            .Select(group => new RemoteZipSegment(group.Key!, [.. group]))
            .OrderBy(segment => segment.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(segment => segment.SegmentIndex)
            .ToArray();
        return new RemoteZipIndex(groups);
    }

    public async Task<string> DownloadAsync(DatasetChunk chunk, RemoteZipSegment segment,
        string extractionRoot, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = segment.Entries.Sum(entry => entry.CompressedSize);
        long completed = 0;
        foreach (var entry in segment.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = SafeRelativePath(entry.Name);
            var destination = Path.Combine(extractionRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && new FileInfo(destination).Length == entry.UncompressedSize)
            {
                completed += entry.CompressedSize;
                progress?.Report(new(completed, total, 0));
                continue;
            }

            var local = await ReadRangeAsync(chunk.Url, entry.LocalHeaderOffset,
                entry.LocalHeaderOffset + 29, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalSignature)
                throw new InvalidDataException($"Invalid local ZIP header for {entry.Name}.");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28, 2));
            var dataOffset = entry.LocalHeaderOffset + 30 + nameLength + extraLength;
            var compressed = entry.CompressedSize == 0 ? [] : await ReadRangeAsync(chunk.Url,
                dataOffset, dataOffset + entry.CompressedSize - 1, cancellationToken);
            var temporary = destination + ".partial";
            try
            {
                await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1 << 20, useAsync: true);
                if (entry.CompressionMethod == 0)
                {
                    await output.WriteAsync(compressed, cancellationToken);
                }
                else if (entry.CompressionMethod == 8)
                {
                    await using var source = new MemoryStream(compressed, writable: false);
                    await using var inflater = new DeflateStream(source, CompressionMode.Decompress);
                    await inflater.CopyToAsync(output, cancellationToken);
                }
                else
                {
                    throw new InvalidDataException($"Unsupported ZIP compression method {entry.CompressionMethod}.");
                }
            }
            catch
            {
                File.Delete(temporary);
                throw;
            }
            if (new FileInfo(temporary).Length != entry.UncompressedSize)
            {
                File.Delete(temporary);
                throw new InvalidDataException($"Extracted size mismatch for {entry.Name}.");
            }
            File.Move(temporary, destination, overwrite: true);
            completed += entry.CompressedSize;
            progress?.Report(new(completed, total, 0));
        }
        return Path.Combine(extractionRoot, SafeRelativePath(segment.Prefix));
    }

    private async Task<byte[]> ReadRangeAsync(string url, long from, long to,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            response.Content.Headers.ContentRange?.From != from ||
            response.Content.Headers.ContentRange?.To != to)
            throw new HttpRequestException("The server did not honor the ZIP byte-range request.");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static List<RemoteZipEntry> ParseCentralDirectory(byte[] bytes, long expectedCount)
    {
        var entries = new List<RemoteZipEntry>();
        var offset = 0;
        while (offset + 46 <= bytes.Length && entries.Count < expectedCount)
        {
            var header = bytes.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralSignature) break;
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(10, 2));
            ulong compressed = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
            ulong uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32, 2));
            ulong localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(42, 4));
            var name = Encoding.UTF8.GetString(header.Slice(46, nameLength)).Replace('\\', '/');
            var extra = header.Slice(46 + nameLength, extraLength);
            ReadZip64(extra, ref uncompressed, ref compressed, ref localOffset);
            if (!name.EndsWith('/'))
                entries.Add(new(name, method, checked((long)compressed),
                    checked((long)uncompressed), checked((long)localOffset)));
            offset += 46 + nameLength + extraLength + commentLength;
        }
        if (entries.Count == 0) throw new InvalidDataException("The ZIP index contains no files.");
        return entries;
    }

    private static void ReadZip64(ReadOnlySpan<byte> extra, ref ulong uncompressed,
        ref ulong compressed, ref ulong localOffset)
    {
        var offset = 0;
        while (offset + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(offset, 2));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(offset + 2, 2));
            var field = extra.Slice(offset + 4, length);
            if (id == 1)
            {
                var position = 0;
                if (uncompressed == uint.MaxValue) { uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(field.Slice(position, 8)); position += 8; }
                if (compressed == uint.MaxValue) { compressed = BinaryPrimitives.ReadUInt64LittleEndian(field.Slice(position, 8)); position += 8; }
                if (localOffset == uint.MaxValue) localOffset = BinaryPrimitives.ReadUInt64LittleEndian(field.Slice(position, 8));
                return;
            }
            offset += 4 + length;
        }
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (var i = bytes.Length - 4; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == signature) return i;
        return -1;
    }

    private static string? SegmentPrefix(string name)
    {
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
            if (int.TryParse(parts[i], out _)) return string.Join('/', parts.Take(i + 1));
        return null;
    }

    private static bool IsBuilderInput(string name) =>
        name.EndsWith("/raw_log.bz2", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/video.hevc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/preview.png", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("/global_pose/", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("/processed_log/CAN/", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("/processed_log/GNSS/", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("/processed_log/IMU/", StringComparison.OrdinalIgnoreCase);

    private static string SafeRelativePath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.Contains(':')))
            throw new InvalidDataException($"Unsafe ZIP path: {name}");
        return Path.Combine(parts.Select(part => part.Replace('|', '_')).ToArray());
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}

public sealed record RemoteZipIndex(IReadOnlyList<RemoteZipSegment> Segments);
public sealed record RemoteZipSegment(string Prefix, IReadOnlyList<RemoteZipEntry> Entries)
{
    public string DisplayName => Prefix.Replace('/', ' ');
    public long DownloadBytes => Entries.Sum(entry => entry.CompressedSize);
    public int SegmentIndex => int.Parse(Prefix.Split('/')[^1]);
    public string Route
    {
        get
        {
            var route = Prefix.Split('/')[^2];
            return route.Length > 16 && route[16] == '_' ? route[..16] + "|" + route[17..] : route;
        }
    }
}
public sealed record RemoteZipEntry(string Name, ushort CompressionMethod, long CompressedSize,
    long UncompressedSize, long LocalHeaderOffset);