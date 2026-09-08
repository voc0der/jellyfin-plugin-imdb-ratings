using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.ScheduledTasks;

public class RefreshImdbRatingsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RefreshImdbRatingsTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataPath;

    public RefreshImdbRatingsTask(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<RefreshImdbRatingsTask> logger,
        ILoggerFactory loggerFactory,
        MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _dataPath = applicationPaths.DataPath;
    }

    public string Name => "Refresh IMDb Ratings";

    public string Key => "RefreshImdbRatings";

    public string Description => "Downloads the IMDb ratings flat file and updates CommunityRating on all library items with an IMDb ID.";

    public string Category => "IMDb Ratings";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        _logger.LogInformation("Starting IMDb ratings refresh (minVotes={MinVotes}, movies={Movies}, series={Series}, seasonAverages={SeasonAverages})",
            config.MinimumVotes, config.IncludeMovies, config.IncludeSeries, config.IncludeSeasonAverages);

        // Reclaim the provider's disk and memory before any network, parsing, or library work can fail. The
        // scheduled rating refresh remains enabled independently and continues below.
        if (!IsMetadataProviderCurrentlyEnabled(config))
        {
            DisableProviderIndex();
        }

        var downloader = new ImdbFlatFileDownloader(
            _httpClientFactory,
            _loggerFactory.CreateLogger<ImdbFlatFileDownloader>(),
            _dataPath);
        var parser = new ImdbRatingsParser(_loggerFactory.CreateLogger<ImdbRatingsParser>());

        // Step 1: Query library items and build a distinct IMDb ID filter set.
        progress.Report(0);
        var items = GetLibraryItems(config);
        if (items.Count == 0)
        {
            _logger.LogInformation("Found 0 library items with IMDb IDs");

            // An empty library still needs an index built, so the provider can rate the very first scan.
            await TryWriteProviderIndexAsync(downloader, parser, config, cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return;
        }

        var libraryImdbIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var imdbId = items[i].GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                libraryImdbIds.Add(imdbId);
            }
        }

        _logger.LogInformation(
            "Found {ItemCount} library items with IMDb IDs ({DistinctIdCount} distinct IDs)",
            items.Count,
            libraryImdbIds.Count);

        if (libraryImdbIds.Count == 0)
        {
            _logger.LogWarning("No valid IMDb IDs found on selected library items — nothing to update");

            await TryWriteProviderIndexAsync(downloader, parser, config, cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return;
        }

        progress.Report(5);

        // Step 2: Download/cache the ratings file, Step 3: Parse ratings (filtered to library IMDb IDs)
        var ratings = await DownloadAndParseWithRetryAsync(
            downloader,
            parser,
            libraryImdbIds,
            progress,
            cancellationToken).ConfigureAwait(false);
        progress.Report(30);
        int lastScanProgressBucket = 30;

        // Step 4: Identify items that need rating updates (without mutating in-memory state)
        var pendingUpdates = new List<PendingRatingUpdate>();
        int skippedMissingImdbId = 0;
        int skippedBelowMinimumVotes = 0;
        int skippedUnchanged = 0;
        int notFound = 0;
        const int debugSampleLimitPerCategory = 10;
        bool enableItemDebugLogging = config.EnableItemDebugLogging && _logger.IsEnabled(LogLevel.Debug);
        int loggedNotFoundDebugSamples = 0;
        int loggedBelowMinimumDebugSamples = 0;

        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            var imdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);

            if (string.IsNullOrEmpty(imdbId))
            {
                skippedMissingImdbId++;
            }
            else if (!ratings.TryGetValue(imdbId, out var ratingData))
            {
                if (enableItemDebugLogging && loggedNotFoundDebugSamples < debugSampleLimitPerCategory)
                {
                    loggedNotFoundDebugSamples++;
                    _logger.LogDebug("IMDb ID {ImdbId} not found in ratings file for \"{Name}\"", imdbId, item.Name);
                }
                notFound++;
            }
            else if (ratingData.Votes < config.MinimumVotes)
            {
                if (enableItemDebugLogging && loggedBelowMinimumDebugSamples < debugSampleLimitPerCategory)
                {
                    loggedBelowMinimumDebugSamples++;
                    _logger.LogDebug("Skipping \"{Name}\" — {Votes} votes below minimum {MinVotes}", item.Name, ratingData.Votes, config.MinimumVotes);
                }
                skippedBelowMinimumVotes++;
            }
            else
            {
                var newRating = ratingData.Rating;
                if (RatingComparison.IsUnchanged(item.CommunityRating, newRating))
                {
                    skippedUnchanged++;
                }
                else
                {
                    pendingUpdates.Add(new PendingRatingUpdate(item, item.GetParent(), item.CommunityRating, newRating));
                }
            }

            double progressPercent = 30 + (60.0 * (i + 1) / items.Count);
            int progressBucket = (int)progressPercent;
            if (progressBucket > lastScanProgressBucket)
            {
                lastScanProgressBucket = progressBucket;
                progress.Report(progressPercent);
            }
        }

        if (enableItemDebugLogging)
        {
            var suppressedNotFoundDebugLines = notFound - loggedNotFoundDebugSamples;
            if (suppressedNotFoundDebugLines > 0)
            {
                _logger.LogDebug(
                    "Suppressed {Count} additional per-item debug logs for IMDb IDs not found in ratings data (sample limit {SampleLimit})",
                    suppressedNotFoundDebugLines,
                    debugSampleLimitPerCategory);
            }

            var suppressedBelowMinimumDebugLines = skippedBelowMinimumVotes - loggedBelowMinimumDebugSamples;
            if (suppressedBelowMinimumDebugLines > 0)
            {
                _logger.LogDebug(
                    "Suppressed {Count} additional per-item debug logs for items below minimum votes (sample limit {SampleLimit})",
                    suppressedBelowMinimumDebugLines,
                    debugSampleLimitPerCategory);
            }
        }

        // Step 4b: Calculate season ratings as the average of eligible IMDb episode ratings
        int seasonUpdated = 0;
        int seasonSkippedNoRatings = 0;
        int seasonSkippedUnchanged = 0;
        if (config.IncludeSeries && config.IncludeSeasonAverages)
        {
            // Reuse the already-fetched episode results and group by Jellyfin's logical season ID.
            var episodeData = items
                .OfType<MediaBrowser.Controller.Entities.TV.Episode>()
                .Where(e => e.SeasonId != Guid.Empty)
                .Select(e => (
                    SeasonId: e.SeasonId,
                    ImdbId: e.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb)));

            var seasonAverages = SeasonRatingCalculator.CalculateSeasonAverages(episodeData, ratings, config.MinimumVotes);

            // Query seasons for rating comparison and parent lookup during save
            var seasonsById = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                IsVirtualItem = false,
                Recursive = true
            }).ToDictionary(s => s.Id);

            foreach (var (seasonId, avgRating) in seasonAverages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!seasonsById.TryGetValue(seasonId, out var season))
                {
                    continue;
                }

                if (RatingComparison.IsUnchanged(season.CommunityRating, avgRating))
                {
                    seasonSkippedUnchanged++;
                    continue;
                }

                pendingUpdates.Add(new PendingRatingUpdate(season, season.GetParent(), season.CommunityRating, avgRating));
                seasonUpdated++;
            }

            seasonSkippedNoRatings = seasonsById.Keys.Count(id => !seasonAverages.ContainsKey(id));

            _logger.LogInformation(
                "Season ratings: {Updated} to update, {SkippedUnchanged} unchanged, {SkippedNoRatings} skipped (no eligible episodes)",
                seasonUpdated, seasonSkippedUnchanged, seasonSkippedNoRatings);
        }

        progress.Report(90);

        // Step 5: Apply ratings and batch save, grouped by parent and chunked
        if (pendingUpdates.Count > 0)
        {
            _logger.LogInformation("Batch saving {Count} updated ratings to database", pendingUpdates.Count);

            await ApplyPendingUpdatesAsync(
                pendingUpdates,
                new LibraryManagerUpdateSink(_libraryManager),
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        // Step 6: Refresh the compact index the scan-time metadata provider reads.
        await TryWriteProviderIndexAsync(downloader, parser, config, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        var skippedTotal = skippedMissingImdbId + skippedBelowMinimumVotes + skippedUnchanged;
        _logger.LogInformation(
            "IMDb ratings refresh complete: {Updated} updated ({SeasonUpdated} seasons from episode averages), {Skipped} skipped ({Unchanged} unchanged, {BelowMinimum} below minimum votes, {MissingImdbId} missing IMDb ID), {NotFound} not found in IMDb ratings",
            pendingUpdates.Count,
            seasonUpdated,
            skippedTotal,
            skippedUnchanged,
            skippedBelowMinimumVotes,
            skippedMissingImdbId,
            notFound);
    }

    /// <summary>
    /// Applies each pending rating and persists it, grouped by parent and chunked.
    /// </summary>
    /// <remarks>
    /// Ratings are written to the in-memory items only immediately before the chunk containing them is saved,
    /// and reverted if that save throws. Jellyfin hands out live <see cref="BaseItem"/> instances, so a chunk
    /// that failed to persist must not leave the running server displaying a rating no database row holds.
    /// </remarks>
    internal static async Task ApplyPendingUpdatesAsync(
        IReadOnlyList<PendingRatingUpdate> pendingUpdates,
        IItemUpdateSink sink,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingUpdates);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(progress);

        const int batchSize = 500;
        var byParent = pendingUpdates.GroupBy(p => p.Parent?.Id ?? Guid.Empty);
        int saved = 0;
        int lastSaveProgressBucket = 90;

        foreach (var group in byParent)
        {
            var parent = group.First().Parent;

            foreach (var chunk in group.Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (parent is null)
                {
                    // Preserve prior semantics for root/null-parent items.
                    for (int j = 0; j < chunk.Length; j++)
                    {
                        chunk[j].Item.CommunityRating = chunk[j].NewRating;
                        try
                        {
                            await sink.UpdateItemAsync(
                                chunk[j].Item,
                                chunk[j].Parent, // Preserve prior behavior for root items with no parent.
                                ItemUpdateType.MetadataEdit,
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            chunk[j].Item.CommunityRating = chunk[j].OldRating;
                            throw;
                        }
                    }
                }
                else
                {
                    // Apply ratings immediately before persisting this chunk.
                    var chunkItems = new BaseItem[chunk.Length];
                    for (int j = 0; j < chunk.Length; j++)
                    {
                        chunk[j].Item.CommunityRating = chunk[j].NewRating;
                        chunkItems[j] = chunk[j].Item;
                    }

                    try
                    {
                        await sink.UpdateItemsAsync(chunkItems, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Revert this chunk's in-memory mutations if the batch save fails/cancels.
                        for (int j = 0; j < chunk.Length; j++)
                        {
                            chunk[j].Item.CommunityRating = chunk[j].OldRating;
                        }

                        throw;
                    }
                }

                saved += chunk.Length;

                double saveProgress = 90 + (10.0 * saved / pendingUpdates.Count);
                int saveProgressBucket = (int)saveProgress;
                if (saveProgressBucket > lastSaveProgressBucket)
                {
                    lastSaveProgressBucket = saveProgressBucket;
                    progress.Report(saveProgress);
                }
            }
        }
    }

    /// <summary>
    /// Builds the <see cref="BaseItemKind"/> filter for the library query, empty when nothing is selected.
    /// </summary>
    internal static BaseItemKind[] BuildIncludeItemTypes(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var includeTypes = new List<BaseItemKind>();
        if (config.IncludeMovies)
        {
            includeTypes.Add(BaseItemKind.Movie);
        }

        if (config.IncludeSeries)
        {
            includeTypes.Add(BaseItemKind.Series);
            includeTypes.Add(BaseItemKind.Episode);
        }

        return includeTypes.ToArray();
    }

    /// <summary>
    /// Rebuilds the compact index used by <see cref="Providers.ImdbRatingsItemProvider"/>.
    /// </summary>
    /// <remarks>
    /// The index is an enhancement rather than part of the refresh contract, so any failure here is logged
    /// and swallowed: the ratings written above are already committed and must not be reported as failed.
    /// The stale index stays in place until the next successful run.
    /// </remarks>
    private async Task TryWriteProviderIndexAsync(
        ImdbFlatFileDownloader downloader,
        ImdbRatingsParser parser,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var indexPath = ImdbRatingsIndex.GetIndexPath(_dataPath);

        if (!IsMetadataProviderCurrentlyEnabled(config))
        {
            // The setting may have changed while the scheduled refresh was running.
            DisableProviderIndex();
            return;
        }

        try
        {
            // The task is the sole writer of the index and may download to build it; the provider never does.
            var ratingsFilePath = await GetRatingsFilePathWithTransientRetryAsync(downloader, cancellationToken)
                .ConfigureAwait(false);

            var index = await parser.BuildIndexAsync(ratingsFilePath, cancellationToken).ConfigureAwait(false);

            // Building can take long enough for the setting to change. Avoid publishing work that was disabled
            // while the task was running.
            if (!IsMetadataProviderCurrentlyEnabled(config))
            {
                DisableProviderIndex();
                return;
            }

            await index.WriteAsync(indexPath, cancellationToken).ConfigureAwait(false);

            // If disable raced the asynchronous write, the configuration callback deleted the old destination
            // before File.Move published this one. Delete the newly published file as the later operation.
            if (!IsMetadataProviderCurrentlyEnabled(config))
            {
                DisableProviderIndex();
                return;
            }

            ImdbRatingsIndexCache.InvalidateShared();

            _logger.LogInformation(
                "Wrote IMDb ratings index: {Count} titles, {SizeMb:F1} MB at {Path}",
                index.Count,
                index.ApproximateSizeInBytes / (1024d * 1024d),
                indexPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build IMDb ratings index; scan-time ratings will use the previous index if present");
        }
    }

    private async Task<Dictionary<string, (float Rating, int Votes)>> DownloadAndParseWithRetryAsync(
        ImdbFlatFileDownloader downloader,
        ImdbRatingsParser parser,
        IReadOnlySet<string> includeImdbIds,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var filePath = await GetRatingsFilePathWithTransientRetryAsync(downloader, cancellationToken).ConfigureAwait(false);
            progress.Report(10);
            return await parser.ParseFilteredAsync(filePath, includeImdbIds, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            // Bad data on disk — invalidate cache and re-download.
            _logger.LogWarning(ex,
                "IMDb ratings data failed validation on first attempt; invalidating cache and retrying");

            downloader.InvalidateCache();
            return await RetryDownloadAndParseAsync(
                downloader,
                parser,
                includeImdbIds,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, (float Rating, int Votes)>> RetryDownloadAndParseAsync(
        ImdbFlatFileDownloader downloader,
        ImdbRatingsParser parser,
        IReadOnlySet<string> includeImdbIds,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            // Cache invalidation above forces a fresh download. Use the same timeout-aware retry path as the
            // initial attempt so an exhausted HttpClient timeout is reported as failure, not cancellation.
            var filePath = await GetRatingsFilePathWithTransientRetryAsync(downloader, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(10);
            return await parser.ParseFilteredAsync(filePath, includeImdbIds, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException retryEx)
        {
            _logger.LogError(retryEx, "IMDb ratings data failed validation after retry");
            throw;
        }
    }

    private async Task<string> GetRatingsFilePathWithTransientRetryAsync(
        ImdbFlatFileDownloader downloader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await downloader.GetRatingsFilePathAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransientNetworkError(ex, cancellationToken))
        {
            // Transient download error — try once more after a short delay, or fall back to stale cache.
            _logger.LogWarning(ex, "Transient network error downloading IMDb ratings; retrying once after delay");

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            try
            {
                return await downloader.GetRatingsFilePathAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryEx) when (IsTransientNetworkError(retryEx, cancellationToken))
            {
                if (!downloader.HasCacheFile)
                {
                    _logger.LogError(retryEx, "Download failed after retry and no cached ratings file exists");

                    // HttpClient represents its own timeout as TaskCanceledException. Jellyfin treats every
                    // OperationCanceledException as a user-cancelled scheduled task, so translate an exhausted
                    // timeout when the scheduler token itself remains active.
                    if (retryEx is OperationCanceledException timeoutException
                        && !cancellationToken.IsCancellationRequested)
                    {
                        throw CreateExhaustedTimeoutException(timeoutException);
                    }

                    throw;
                }

                _logger.LogWarning(retryEx,
                    "Download failed after retry; falling back to stale cache at {Path}", downloader.CachePath);

                return downloader.CachePath;
            }
        }
    }

    internal static bool IsTransientNetworkError(Exception ex, CancellationToken cancellationToken)
    {
        return ex is HttpRequestException
            || (ex is IOException && ex is not InvalidDataException)
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);
    }

    internal static bool ResolveMetadataProviderEnabled(
        PluginConfiguration taskConfiguration,
        PluginConfiguration? currentConfiguration)
    {
        ArgumentNullException.ThrowIfNull(taskConfiguration);
        return (currentConfiguration ?? taskConfiguration).EnableMetadataProvider;
    }

    internal static HttpRequestException CreateExhaustedTimeoutException(OperationCanceledException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new HttpRequestException("IMDb ratings download timed out after retry.", exception);
    }

    private static bool IsMetadataProviderCurrentlyEnabled(PluginConfiguration taskConfiguration)
    {
        return ResolveMetadataProviderEnabled(taskConfiguration, Plugin.Instance?.Configuration);
    }

    private void DisableProviderIndex()
    {
        var indexPath = ImdbRatingsIndex.GetIndexPath(_dataPath);

        try
        {
            if (File.Exists(indexPath))
            {
                File.Delete(indexPath);
                _logger.LogInformation("Scan-time provider disabled; removed IMDb ratings index at {Path}", indexPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to remove IMDb ratings index at {Path}", indexPath);
        }

        // Drop the loaded copy even if disk cleanup failed; a disabled provider must not retain its memory.
        ImdbRatingsIndexCache.InvalidateShared();
    }

    private IReadOnlyList<BaseItem> GetLibraryItems(PluginConfiguration config)
    {
        var query = new InternalItemsQuery
        {
            HasImdbId = true,
            IsVirtualItem = false,
            Recursive = true
        };

        var includeTypes = BuildIncludeItemTypes(config);
        if (includeTypes.Length == 0)
        {
            _logger.LogWarning("No library types selected — nothing to update");
            return Array.Empty<BaseItem>();
        }

        query.IncludeItemTypes = includeTypes;

        return _libraryManager.GetItemList(query);
    }

    /// <summary>
    /// The subset of <see cref="ILibraryManager"/> the batch-save loop needs, so the loop is testable
    /// without standing up the full 100-method interface.
    /// </summary>
    internal interface IItemUpdateSink
    {
        Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType updateReason, CancellationToken cancellationToken);

        Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem parent, ItemUpdateType updateReason, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A single rating change, captured before anything is mutated so a failed save can be undone.
    /// </summary>
    internal readonly record struct PendingRatingUpdate(BaseItem Item, BaseItem? Parent, float? OldRating, float NewRating);

    private sealed class LibraryManagerUpdateSink : IItemUpdateSink
    {
        private readonly ILibraryManager _libraryManager;

        public LibraryManagerUpdateSink(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType updateReason, CancellationToken cancellationToken)
            => _libraryManager.UpdateItemAsync(item, parent!, updateReason, cancellationToken);

        public Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem parent, ItemUpdateType updateReason, CancellationToken cancellationToken)
            => _libraryManager.UpdateItemsAsync(items, parent, updateReason, cancellationToken);
    }
}
