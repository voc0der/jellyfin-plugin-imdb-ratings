using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// Covers the batch-save loop, whose defining behaviour is that a failed save must leave no trace in memory.
/// Jellyfin hands out live item instances, so a rating that was applied but not persisted would be visible
/// in the running server until restart while no database row backs it.
/// </summary>
public class RefreshImdbRatingsTaskSaveTests
{
    [Fact]
    public async Task ApplyPendingUpdatesAsync_Success_AppliesEveryRatingAndSavesOnce()
    {
        var parent = Folder("parent");
        var first = MovieWith(6.0f);
        var second = MovieWith(null);
        var updates = new[]
        {
            new RefreshImdbRatingsTask.PendingRatingUpdate(first, parent, first.CommunityRating, 8.1f),
            new RefreshImdbRatingsTask.PendingRatingUpdate(second, parent, second.CommunityRating, 7.2f)
        };
        var sink = new RecordingSink();

        await RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None);

        Assert.Equal(8.1f, first.CommunityRating!.Value, 3);
        Assert.Equal(7.2f, second.CommunityRating!.Value, 3);
        Assert.Single(sink.BatchCalls);
        Assert.Equal(2, sink.BatchCalls[0].Items.Count);
        Assert.Same(parent, sink.BatchCalls[0].Parent);
        Assert.Equal(ItemUpdateType.MetadataEdit, sink.BatchCalls[0].UpdateReason);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_BatchSaveThrows_RevertsEveryRatingInThatChunk()
    {
        var parent = Folder("parent");
        var withPrevious = MovieWith(6.0f);
        var withoutPrevious = MovieWith(null);
        var updates = new[]
        {
            new RefreshImdbRatingsTask.PendingRatingUpdate(withPrevious, parent, withPrevious.CommunityRating, 8.1f),
            new RefreshImdbRatingsTask.PendingRatingUpdate(withoutPrevious, parent, withoutPrevious.CommunityRating, 7.2f)
        };
        var sink = new RecordingSink { BatchFailure = () => new InvalidOperationException("database is locked") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None));

        Assert.Equal(6.0f, withPrevious.CommunityRating!.Value, 3);
        Assert.Null(withoutPrevious.CommunityRating);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_BatchSaveCanceled_RevertsEveryRatingInThatChunk()
    {
        var parent = Folder("parent");
        var movie = MovieWith(6.0f);
        var updates = new[]
        {
            new RefreshImdbRatingsTask.PendingRatingUpdate(movie, parent, movie.CommunityRating, 8.1f)
        };
        var sink = new RecordingSink { BatchFailure = () => new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None));

        Assert.Equal(6.0f, movie.CommunityRating!.Value, 3);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_LaterChunkFails_EarlierSavedChunksKeepTheirRatings()
    {
        // 501 items under one parent split into chunks of 500 and 1; only the second chunk fails.
        var parent = Folder("parent");
        var updates = new List<RefreshImdbRatingsTask.PendingRatingUpdate>();
        for (int i = 0; i < 501; i++)
        {
            var movie = MovieWith(1.0f);
            updates.Add(new RefreshImdbRatingsTask.PendingRatingUpdate(movie, parent, movie.CommunityRating, 9.0f));
        }

        var sink = new RecordingSink { FailBatchAtCall = 2, BatchFailure = () => new InvalidOperationException("boom") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None));

        // First chunk persisted, so its in-memory ratings stand.
        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(9.0f, updates[i].Item.CommunityRating!.Value, 3);
        }

        // The failed chunk is rolled back.
        Assert.Equal(1.0f, updates[500].Item.CommunityRating!.Value, 3);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_ChunksAtFiveHundredPerSave()
    {
        var parent = Folder("parent");
        var updates = new List<RefreshImdbRatingsTask.PendingRatingUpdate>();
        for (int i = 0; i < 1200; i++)
        {
            var movie = MovieWith(null);
            updates.Add(new RefreshImdbRatingsTask.PendingRatingUpdate(movie, parent, null, 7.0f));
        }

        var sink = new RecordingSink();

        await RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None);

        Assert.Equal(new[] { 500, 500, 200 }, sink.BatchCalls.Select(c => c.Items.Count).ToArray());
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_ItemsUnderDifferentParents_AreSavedSeparately()
    {
        var firstParent = Folder("a");
        var secondParent = Folder("b");
        var underFirst = MovieWith(null);
        var underSecond = MovieWith(null);
        var updates = new[]
        {
            new RefreshImdbRatingsTask.PendingRatingUpdate(underFirst, firstParent, null, 7.0f),
            new RefreshImdbRatingsTask.PendingRatingUpdate(underSecond, secondParent, null, 8.0f)
        };
        var sink = new RecordingSink();

        await RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None);

        Assert.Equal(2, sink.BatchCalls.Count);
        Assert.Contains(sink.BatchCalls, c => ReferenceEquals(c.Parent, firstParent));
        Assert.Contains(sink.BatchCalls, c => ReferenceEquals(c.Parent, secondParent));
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_NullParent_SavesItemsIndividually()
    {
        var first = MovieWith(null);
        var second = MovieWith(null);
        var updates = new[]
        {
            new RefreshImdbRatingsTask.PendingRatingUpdate(first, null, null, 7.0f),
            new RefreshImdbRatingsTask.PendingRatingUpdate(second, null, null, 8.0f)
        };
        var sink = new RecordingSink();

        await RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None);

        Assert.Equal(2, sink.SingleCalls.Count);
        Assert.Empty(sink.BatchCalls);
        Assert.Equal(7.0f, first.CommunityRating!.Value, 3);
        Assert.Equal(8.0f, second.CommunityRating!.Value, 3);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_NullParentSaveThrows_RevertsOnlyTheFailedItem()
    {
        var saved = MovieWith(2.0f);
        var failed = MovieWith(3.0f);
        var updates = new[]
        {
            new RefreshImdbRatingsTask.PendingRatingUpdate(saved, null, saved.CommunityRating, 7.0f),
            new RefreshImdbRatingsTask.PendingRatingUpdate(failed, null, failed.CommunityRating, 8.0f)
        };
        var sink = new RecordingSink { FailSingleAtCall = 2, SingleFailure = () => new InvalidOperationException("boom") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), CancellationToken.None));

        Assert.Equal(7.0f, saved.CommunityRating!.Value, 3);
        Assert.Equal(3.0f, failed.CommunityRating!.Value, 3);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_AlreadyCanceled_SavesNothingAndMutatesNothing()
    {
        var parent = Folder("parent");
        var movie = MovieWith(4.0f);
        var updates = new[]
        {
            new RefreshImdbRatingsTask.PendingRatingUpdate(movie, parent, movie.CommunityRating, 9.0f)
        };
        var sink = new RecordingSink();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, sink, new NoopProgress(), cts.Token));

        Assert.Equal(4.0f, movie.CommunityRating!.Value, 3);
        Assert.Empty(sink.BatchCalls);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_ReportsProgressBetweenNinetyAndOneHundred()
    {
        var parent = Folder("parent");
        var updates = new List<RefreshImdbRatingsTask.PendingRatingUpdate>();
        for (int i = 0; i < 1200; i++)
        {
            var movie = MovieWith(null);
            updates.Add(new RefreshImdbRatingsTask.PendingRatingUpdate(movie, parent, null, 7.0f));
        }

        var progress = new RecordingProgress();

        await RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(updates, new RecordingSink(), progress, CancellationToken.None);

        Assert.NotEmpty(progress.Reports);
        Assert.All(progress.Reports, value => Assert.InRange(value, 90.0, 100.0));
        Assert.Equal(100.0, progress.Reports[^1], 3);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(null!, new RecordingSink(), new NoopProgress(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(Array.Empty<RefreshImdbRatingsTask.PendingRatingUpdate>(), null!, new NoopProgress(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => RefreshImdbRatingsTask.ApplyPendingUpdatesAsync(Array.Empty<RefreshImdbRatingsTask.PendingRatingUpdate>(), new RecordingSink(), null!, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, true, new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode })]
    [InlineData(true, false, new[] { BaseItemKind.Movie })]
    [InlineData(false, true, new[] { BaseItemKind.Series, BaseItemKind.Episode })]
    public void BuildIncludeItemTypes_SelectsTypesForEachToggle(bool movies, bool series, BaseItemKind[] expected)
    {
        var config = new PluginConfiguration { IncludeMovies = movies, IncludeSeries = series };

        Assert.Equal(expected, RefreshImdbRatingsTask.BuildIncludeItemTypes(config));
    }

    [Fact]
    public void BuildIncludeItemTypes_NothingSelected_ReturnsEmptySoTheLibraryIsNeverQueried()
    {
        var config = new PluginConfiguration { IncludeMovies = false, IncludeSeries = false };

        Assert.Empty(RefreshImdbRatingsTask.BuildIncludeItemTypes(config));
    }

    private static Movie MovieWith(float? communityRating)
    {
        var movie = new Movie { Name = "Test", Id = Guid.NewGuid(), CommunityRating = communityRating };
        movie.ProviderIds[MetadataProvider.Imdb.ToString()] = "tt0000001";
        return movie;
    }

    private static Folder Folder(string name) => new() { Name = name, Id = Guid.NewGuid() };

    private sealed class RecordingSink : RefreshImdbRatingsTask.IItemUpdateSink
    {
        public List<(IReadOnlyList<BaseItem> Items, BaseItem Parent, ItemUpdateType UpdateReason)> BatchCalls { get; } = new();

        public List<(BaseItem Item, BaseItem? Parent, ItemUpdateType UpdateReason)> SingleCalls { get; } = new();

        public Func<Exception>? BatchFailure { get; set; }

        public Func<Exception>? SingleFailure { get; set; }

        /// <summary>Gets or sets the 1-based batch call that should fail; 0 fails the first when a failure is set.</summary>
        public int FailBatchAtCall { get; set; }

        public int FailSingleAtCall { get; set; }

        public Task UpdateItemAsync(BaseItem item, BaseItem? parent, ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            SingleCalls.Add((item, parent, updateReason));
            if (SingleFailure is not null && (FailSingleAtCall == 0 || SingleCalls.Count == FailSingleAtCall))
            {
                throw SingleFailure();
            }

            return Task.CompletedTask;
        }

        public Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem parent, ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            BatchCalls.Add((items.ToArray(), parent, updateReason));
            if (BatchFailure is not null && (FailBatchAtCall == 0 || BatchCalls.Count == FailBatchAtCall))
            {
                throw BatchFailure();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = new();

        public void Report(double value) => Reports.Add(value);
    }

    private sealed class NoopProgress : IProgress<double>
    {
        public void Report(double value)
        {
        }
    }
}
