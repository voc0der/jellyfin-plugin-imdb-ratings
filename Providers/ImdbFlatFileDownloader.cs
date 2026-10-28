using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

public class ImdbFlatFileDownloader
{
    private const string ImdbRatingsUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";
    private const long MaxDecompressedSize = 100 * 1024 * 1024; // 100 MB
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(23);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImdbFlatFileDownloader> _logger;
    private readonly string _cachePath;
    private bool _refreshRequired;

    public ImdbFlatFileDownloader(
        IHttpClientFactory httpClientFactory,
        ILogger<ImdbFlatFileDownloader> logger,
        string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
        var cacheDirectoryPath = Path.Join(dataPath, "imdb-ratings-cache");
        _cachePath = Path.Join(cacheDirectoryPath, "title.ratings.tsv");
    }

    public string CachePath => _cachePath;

    public bool HasCacheFile => File.Exists(_cachePath);

    public async Task<string> GetRatingsFilePathAsync(CancellationToken cancellationToken)
    {
        if (!_refreshRequired && IsCacheFresh())
        {
            _logger.LogInformation("IMDb ratings cache is fresh, skipping download");
            return _cachePath;
        }

        await DownloadAndDecompressAsync(cancellationToken).ConfigureAwait(false);
        _refreshRequired = false;
        return _cachePath;
    }

    /// <summary>
    /// Downloads on every request, however fresh the cache is, until one download succeeds.
    /// </summary>
    /// <remarks>
    /// A failed download never touches the cache file, so it stays available as a fallback.
    /// </remarks>
    public void RequireRefresh()
    {
        _refreshRequired = true;
    }

    public bool InvalidateCache()
    {
        var invalidated = false;

        invalidated |= TryDeleteFile(_cachePath);
        // Clean up any stale temp file from a previous failed download attempt.
        invalidated |= TryDeleteFile(_cachePath + ".tmp");

        return invalidated;
    }

    private bool IsCacheFresh()
    {
        if (!File.Exists(_cachePath))
        {
            return false;
        }

        var lastWrite = File.GetLastWriteTimeUtc(_cachePath);
        return DateTime.UtcNow - lastWrite < CacheMaxAge;
    }

    private async Task DownloadAndDecompressAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Downloading IMDb ratings from {Url}", ImdbRatingsUrl);

        var cacheDir = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(cacheDir);

        var tempPath = _cachePath + ".tmp";

        var client = _httpClientFactory.CreateClient("ImdbRatings");
        using var response = await client.GetAsync(ImdbRatingsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        try
        {
            using var compressedStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await CopyWithLimitAsync(gzipStream, fileStream, MaxDecompressedSize, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        File.Move(tempPath, _cachePath, overwrite: true);
        _logger.LogInformation("IMDb ratings file downloaded and decompressed to {Path}", _cachePath);
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalBytes += bytesRead;
            if (totalBytes > maxBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed data exceeded maximum allowed size of {maxBytes / (1024 * 1024)} MB.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache file {Path}", path);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache file {Path}", path);
            return false;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache file {Path}", path);
            return false;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache file {Path}", path);
            return false;
        }
    }
}
