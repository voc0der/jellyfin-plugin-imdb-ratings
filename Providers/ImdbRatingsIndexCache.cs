using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

/// <summary>
/// Holds the loaded <see cref="ImdbRatingsIndex"/> for the lifetime of the process, reloading it when the
/// scheduled task replaces the file on disk.
/// </summary>
/// <remarks>
/// The scheduled task is the sole writer of the index and this cache is a pure reader: it never downloads
/// and never builds. That keeps scan-time lookups free of network I/O and means a fresh install simply has
/// no ratings to serve until the task has run once.
/// </remarks>
public sealed class ImdbRatingsIndexCache
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);
    private static readonly object SharedLock = new();

    private static ImdbRatingsIndexCache? _shared;
    private static string? _sharedIndexPath;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly string _indexPath;
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Task<ImdbRatingsIndex?>> _loadIndexAsync;
    private readonly Func<string, DateTime> _getWriteTimeUtc;
    private readonly TimeProvider _timeProvider;

    private ImdbRatingsIndex? _index;
    private DateTime _observedWriteTimeUtc;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private bool _hasChecked;
    private long _generation;

    public ImdbRatingsIndexCache(string indexPath, ILogger logger)
        : this(indexPath, logger, ImdbRatingsIndex.TryLoadAsync, TimeProvider.System, GetWriteTimeUtc)
    {
    }

    internal ImdbRatingsIndexCache(
        string indexPath,
        ILogger logger,
        Func<string, CancellationToken, Task<ImdbRatingsIndex?>> loadIndexAsync,
        TimeProvider? timeProvider = null,
        Func<string, DateTime>? getWriteTimeUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loadIndexAsync);

        _indexPath = indexPath;
        _logger = logger;
        _loadIndexAsync = loadIndexAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _getWriteTimeUtc = getWriteTimeUtc ?? GetWriteTimeUtc;
    }

    /// <summary>
    /// Gets the process-wide cache for a given index path.
    /// </summary>
    /// <remarks>
    /// The loaded index is roughly 15 MB, so it is shared rather than held per provider instance: Jellyfin
    /// makes no guarantee about how many times a metadata provider type is constructed.
    /// </remarks>
    public static ImdbRatingsIndexCache GetShared(string indexPath, ILogger logger)
    {
        lock (SharedLock)
        {
            if (_shared is null || !string.Equals(_sharedIndexPath, indexPath, StringComparison.Ordinal))
            {
                _shared = new ImdbRatingsIndexCache(indexPath, logger);
                _sharedIndexPath = indexPath;
            }

            return _shared;
        }
    }

    /// <summary>
    /// Releases the process-wide cache, if one has been created. Used by the scheduled task so a rebuilt or
    /// deleted index takes effect immediately rather than at the next recheck interval.
    /// </summary>
    public static void InvalidateShared()
    {
        lock (SharedLock)
        {
            _shared?.Invalidate();
        }
    }

    /// <summary>
    /// Gets the current index, reloading from disk when the file has changed since the last check.
    /// Returns <see langword="null"/> when no usable index exists yet.
    /// </summary>
    public async Task<ImdbRatingsIndex?> GetIndexAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // A negative result is cached just like a positive one: without this, every scanned item on a server
            // with no index would stat the file, and every item on a server with a corrupt index would re-read
            // and re-validate the whole thing behind the gate.
            if (TryGetFreshConclusion(out var cachedIndex))
            {
                return cachedIndex;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            long attemptGeneration = -1;
            try
            {
                // Another caller may have refreshed while this one waited on the gate.
                if (TryGetFreshConclusion(out cachedIndex))
                {
                    return cachedIndex;
                }

                long generation;
                DateTime observedWriteTimeUtc;
                ImdbRatingsIndex? previousIndex;
                lock (_stateLock)
                {
                    generation = _generation;
                    attemptGeneration = generation;
                    observedWriteTimeUtc = _observedWriteTimeUtc;
                    previousIndex = _index;
                }

                var writeTimeUtc = GetWriteTimeUtcOrDefault();
                if (writeTimeUtc == default)
                {
                    if (!TryCommitConclusion(generation, default, null, out _))
                    {
                        continue;
                    }

                    if (previousIndex is not null)
                    {
                        _logger.LogInformation("IMDb ratings index disappeared from {Path}; scan-time ratings paused", _indexPath);
                    }

                    return null;
                }

                // The file is unchanged since the last evaluation, so the previous conclusion still holds —
                // including the conclusion that it was unreadable.
                if (writeTimeUtc == observedWriteTimeUtc)
                {
                    if (TryRefreshConclusion(generation, out var unchangedIndex))
                    {
                        return unchangedIndex;
                    }

                    continue;
                }

                // Do not publish the new mtime until the cancellable load has reached a completed conclusion.
                // A cancellation therefore leaves the old state retryable by the next caller.
                var loaded = await _loadIndexAsync(_indexPath, cancellationToken).ConfigureAwait(false);
                if (loaded is null)
                {
                    if (!TryCommitConclusion(generation, writeTimeUtc, null, out _))
                    {
                        continue;
                    }

                    _logger.LogWarning(
                        "IMDb ratings index at {Path} is unreadable or has an unexpected format; it will be rebuilt on the next scheduled run",
                        _indexPath);
                    return null;
                }

                if (!TryCommitConclusion(generation, writeTimeUtc, loaded, out var committedIndex))
                {
                    // Invalidation won while the file was being read. Discard this generation and retry the
                    // same lookup so the current item can use the rebuilt index rather than recording a miss.
                    continue;
                }

                _logger.LogInformation(
                    "Loaded IMDb ratings index: {Count} titles, {SizeMb:F1} MB resident",
                    loaded.Count,
                    loaded.ApproximateSizeInBytes / (1024d * 1024d));

                return committedIndex;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to load IMDb ratings index from {Path}", _indexPath);

                // Throttle a transient failure, but retain the last good index and the previously observed mtime.
                // Because the failed mtime is not committed, it will be retried after the interval expires.
                if (TryRecordTransientFailure(attemptGeneration, out var fallbackIndex))
                {
                    return fallbackIndex;
                }

                continue;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Releases the cached index so its memory can be reclaimed, and forces the next lookup to reload.
    /// </summary>
    public void Invalidate()
    {
        lock (_stateLock)
        {
            _generation++;
            _index = null;
            _observedWriteTimeUtc = default;
            _lastCheckUtc = DateTime.MinValue;
            _hasChecked = false;
        }
    }

    private bool TryGetFreshConclusion(out ImdbRatingsIndex? index)
    {
        lock (_stateLock)
        {
            if (_hasChecked && GetUtcNow() - _lastCheckUtc < RecheckInterval)
            {
                index = _index;
                return true;
            }

            index = null;
            return false;
        }
    }

    private bool TryCommitConclusion(
        long generation,
        DateTime observedWriteTimeUtc,
        ImdbRatingsIndex? index,
        out ImdbRatingsIndex? committedIndex)
    {
        lock (_stateLock)
        {
            if (_generation != generation)
            {
                committedIndex = null;
                return false;
            }

            _index = index;
            _observedWriteTimeUtc = observedWriteTimeUtc;
            _lastCheckUtc = GetUtcNow();
            _hasChecked = true;
            committedIndex = index;
            return true;
        }
    }

    private bool TryRefreshConclusion(long generation, out ImdbRatingsIndex? index)
    {
        lock (_stateLock)
        {
            if (_generation != generation)
            {
                index = null;
                return false;
            }

            _lastCheckUtc = GetUtcNow();
            _hasChecked = true;
            index = _index;
            return true;
        }
    }

    private bool TryRecordTransientFailure(long generation, out ImdbRatingsIndex? index)
    {
        lock (_stateLock)
        {
            // Invalidation clears _hasChecked and advances the generation while a load is running. Do not
            // turn that invalidated state back into a cached conclusion from this older attempt.
            if (_generation != generation)
            {
                index = null;
                return false;
            }

            _lastCheckUtc = GetUtcNow();
            _hasChecked = true;
            index = _index;
            return true;
        }
    }

    private DateTime GetWriteTimeUtcOrDefault()
    {
        try
        {
            return _getWriteTimeUtc(_indexPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return default;
        }
    }

    private static DateTime GetWriteTimeUtc(string indexPath)
    {
        using var handle = File.OpenHandle(
            indexPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return File.GetLastWriteTimeUtc(handle);
    }

    private DateTime GetUtcNow()
    {
        return _timeProvider.GetUtcNow().UtcDateTime;
    }
}
