using System.IO.Compression;
using System.Net;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// Covers the download/cache layer, including the decompressed-size limit that stops a hostile or corrupt
/// gzip response from filling the server's disk.
/// </summary>
public class ImdbFlatFileDownloaderTests
{
    // Mirrors ImdbFlatFileDownloader.MaxDecompressedSize.
    private const long MaxDecompressedSize = 100 * 1024 * 1024;

    // Mirrors ImdbFlatFileDownloader.CacheMaxAge.
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(23);

    [Fact]
    public async Task GetRatingsFilePathAsync_NoCache_DownloadsAndDecompresses()
    {
        using var temp = new TempDirectory();
        const string payload = "tconst\taverageRating\tnumVotes\ntt0000001\t7.5\t100\n";
        var handler = new StubHandler(GzipOf(payload));
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        var path = await downloader.GetRatingsFilePathAsync(CancellationToken.None);

        Assert.Equal(downloader.CachePath, path);
        Assert.Equal(payload, await File.ReadAllTextAsync(path));
        Assert.Equal(1, handler.RequestCount);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task GetRatingsFilePathAsync_DecompressedPayloadExceedsLimit_ThrowsAndLeavesNoFiles()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(GzipOfZeroBytes(MaxDecompressedSize + (1024 * 1024)));
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => downloader.GetRatingsFilePathAsync(CancellationToken.None));

        Assert.Contains("100 MB", ex.Message, StringComparison.Ordinal);

        // The partial write must not survive as either a temp file or a usable cache.
        Assert.False(File.Exists(downloader.CachePath + ".tmp"));
        Assert.False(File.Exists(downloader.CachePath));
        Assert.False(downloader.HasCacheFile);
    }

    [Fact]
    public async Task GetRatingsFilePathAsync_OversizedPayload_LeavesPreviousCacheIntact()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(GzipOfZeroBytes(MaxDecompressedSize + (1024 * 1024)));
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        // A stale but valid cache from a previous run.
        Directory.CreateDirectory(Path.GetDirectoryName(downloader.CachePath)!);
        await File.WriteAllTextAsync(downloader.CachePath, "previous good data");
        File.SetLastWriteTimeUtc(downloader.CachePath, DateTime.UtcNow - CacheMaxAge - TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => downloader.GetRatingsFilePathAsync(CancellationToken.None));

        Assert.Equal("previous good data", await File.ReadAllTextAsync(downloader.CachePath));
    }

    [Fact]
    public async Task GetRatingsFilePathAsync_HttpError_ThrowsAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(Array.Empty<byte>(), HttpStatusCode.ServiceUnavailable);
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.GetRatingsFilePathAsync(CancellationToken.None));

        Assert.False(File.Exists(downloader.CachePath));
        Assert.False(File.Exists(downloader.CachePath + ".tmp"));
    }

    [Fact]
    public async Task GetRatingsFilePathAsync_NotGzipAtAll_ThrowsAndCleansUpTempFile()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(System.Text.Encoding.UTF8.GetBytes("<html>502 Bad Gateway</html>"));
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.GetRatingsFilePathAsync(CancellationToken.None));

        Assert.False(File.Exists(downloader.CachePath + ".tmp"));
        Assert.False(File.Exists(downloader.CachePath));
    }

    [Fact]
    public async Task GetRatingsFilePathAsync_TruncatedGzipHeader_WritesEmptyCacheForTheParserToReject()
    {
        // GZipStream treats a stream that ends inside the header as a clean end-of-data rather than an error,
        // so this path produces an empty cache file instead of throwing. That is caught one layer up: the
        // parser rejects a file with no header, and the task then invalidates the cache and re-downloads.
        using var temp = new TempDirectory();
        var handler = new StubHandler(new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0xde, 0xad, 0xbe, 0xef });
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        var path = await downloader.GetRatingsFilePathAsync(CancellationToken.None);

        Assert.Empty(await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(downloader.CachePath + ".tmp"));

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task GetRatingsFilePathAsync_FreshCache_SkipsDownloadEntirely()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(GzipOf("should not be requested"));
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(downloader.CachePath)!);
        await File.WriteAllTextAsync(downloader.CachePath, "cached");
        File.SetLastWriteTimeUtc(downloader.CachePath, DateTime.UtcNow - CacheMaxAge + TimeSpan.FromMinutes(30));

        var path = await downloader.GetRatingsFilePathAsync(CancellationToken.None);

        Assert.Equal("cached", await File.ReadAllTextAsync(path));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetRatingsFilePathAsync_CacheOlderThanMaxAge_RedownloadsAndReplaces()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(GzipOf("fresh data"));
        var downloader = new ImdbFlatFileDownloader(new StubHttpClientFactory(handler), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(downloader.CachePath)!);
        await File.WriteAllTextAsync(downloader.CachePath, "stale data");
        File.SetLastWriteTimeUtc(downloader.CachePath, DateTime.UtcNow - CacheMaxAge - TimeSpan.FromMinutes(1));

        var path = await downloader.GetRatingsFilePathAsync(CancellationToken.None);

        Assert.Equal("fresh data", await File.ReadAllTextAsync(path));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task InvalidateCache_RemovesBothCacheAndStaleTempFile()
    {
        using var temp = new TempDirectory();
        var downloader = new ImdbFlatFileDownloader(
            new StubHttpClientFactory(new StubHandler(Array.Empty<byte>())), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(downloader.CachePath)!);
        await File.WriteAllTextAsync(downloader.CachePath, "cached");
        await File.WriteAllTextAsync(downloader.CachePath + ".tmp", "leftover from a failed run");

        Assert.True(downloader.InvalidateCache());

        Assert.False(File.Exists(downloader.CachePath));
        Assert.False(File.Exists(downloader.CachePath + ".tmp"));
    }

    [Fact]
    public void InvalidateCache_NothingCached_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var downloader = new ImdbFlatFileDownloader(
            new StubHttpClientFactory(new StubHandler(Array.Empty<byte>())), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        Assert.False(downloader.InvalidateCache());
        Assert.False(downloader.HasCacheFile);
    }

    [Fact]
    public void CachePath_SitsBesideTheProviderIndex()
    {
        using var temp = new TempDirectory();
        var downloader = new ImdbFlatFileDownloader(
            new StubHttpClientFactory(new StubHandler(Array.Empty<byte>())), NullLogger<ImdbFlatFileDownloader>.Instance, temp.Path);

        Assert.Equal(
            Path.GetDirectoryName(ImdbRatingsIndex.GetIndexPath(temp.Path)),
            Path.GetDirectoryName(downloader.CachePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankDataPath_Throws(string dataPath)
    {
        Assert.Throws<ArgumentException>(() => new ImdbFlatFileDownloader(
            new StubHttpClientFactory(new StubHandler(Array.Empty<byte>())),
            NullLogger<ImdbFlatFileDownloader>.Instance,
            dataPath));
    }

    private static byte[] GzipOf(string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A highly compressible payload that expands past the limit: the point of the guard is that a small
    /// response body can still decompress into an arbitrarily large file.
    /// </summary>
    private static byte[] GzipOfZeroBytes(long totalBytes)
    {
        var chunk = new byte[1024 * 1024];
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            long written = 0;
            while (written < totalBytes)
            {
                var count = (int)Math.Min(chunk.Length, totalBytes - written);
                gzip.Write(chunk, 0, count);
                written += count;
            }
        }

        return output.ToArray();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly HttpStatusCode _status;

        public StubHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body)
            });
        }
    }
}
