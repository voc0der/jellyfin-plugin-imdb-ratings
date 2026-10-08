using System.IO.Compression;
using System.Net;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Jellyfin.Plugin.ImdbRatings.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

[Collection("Plugin state")]
public class RefreshImdbRatingsTaskExecutionTests(RatingsDataset dataset) : IClassFixture<RatingsDataset>
{
    [Fact]
    public async Task ExecuteAsync_MixedLibrary_SavesEligibleRatingsAndBoundsDebugSamples()
    {
        using var scope = new PluginTestScope();
        scope.Configuration.MinimumVotes = 100;
        scope.Configuration.EnableItemDebugLogging = true;
        dataset.WriteCache(scope.Temp.Path);
        var parent = new Folder { Id = Guid.NewGuid() };
        var changed = Movie("tt0000001", 4f, parent);
        var root = Movie("tt0000002", null);
        var unchanged = Movie("tt0000001", 8f);
        var missingId = Movie(null, 5f);
        var belowMinimum = Enumerable.Range(0, 12).Select(_ => Movie("tt0000003", 5f)).ToArray();
        var notFound = Enumerable.Range(0, 12).Select(_ => Movie("tt9999999", 5f)).ToArray();
        BaseItem[] items = [changed, root, unchanged, missingId, .. belowMinimum, .. notFound];
        var library = new RecordingLibrary(items, parents: [parent]);
        using var http = new RatingsHttpClientFactory();
        var logger = new RecordingLogger<RefreshImdbRatingsTask>();
        var progress = new RecordingProgress();

        await CreateTask(scope, library, http, logger).ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(8f, changed.CommunityRating);
        Assert.Equal(6f, root.CommunityRating);
        Assert.Equal(8f, unchanged.CommunityRating);
        Assert.Equal(5f, missingId.CommunityRating);
        Assert.All(belowMinimum.Concat(notFound), item => Assert.Equal(5f, item.CommunityRating));
        var batch = Assert.Single(library.BatchSaves);
        Assert.Same(parent, batch.Parent);
        Assert.Same(changed, Assert.Single(batch.Items));
        Assert.Same(root, Assert.Single(library.SingleSaves));
        var query = Assert.Single(library.Queries);
        Assert.True(query.HasImdbId);
        Assert.False(query.IsVirtualItem);
        Assert.True(query.Recursive);
        Assert.Equal(new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode }, query.IncludeItemTypes);
        Assert.Equal("2 ratings updated; 14 skipped; 12 not found.", ReadRun(scope).Summary);
        Assert.Equal("Completed", ReadRun(scope).Status);
        Assert.Equal(0, http.Calls);
        Assert.Equal(0, progress.Values[0]);
        Assert.Equal(100, progress.Values[^1]);
        Assert.Equal(progress.Values.Order(), progress.Values);
        Assert.Equal(10, logger.Messages.Count(m => m.Level == LogLevel.Debug && m.Message.Contains("not found in ratings file")));
        Assert.Equal(10, logger.Messages.Count(m => m.Level == LogLevel.Debug && m.Message.StartsWith("Skipping ")));
        Assert.Equal(2, logger.Messages.Count(m => m.Message.StartsWith("Suppressed 2 additional")));
        var index = await ImdbRatingsIndex.TryLoadAsync(scope.IndexPath, CancellationToken.None);
        Assert.NotNull(index);
        Assert.True(index.TryGetRating("tt0500000", 1, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SeasonAverages_UpdatesOnlyEnabledSeasonsWithChangedEligibleRatings(bool enabled)
    {
        using var scope = new PluginTestScope();
        scope.Configuration.IncludeSeasonAverages = enabled;
        scope.Configuration.MinimumVotes = 100;
        dataset.WriteCache(scope.Temp.Path);
        var changed = new Season { Id = Guid.NewGuid(), CommunityRating = 4f };
        var unchanged = new Season { Id = Guid.NewGuid(), CommunityRating = 8f };
        var ineligible = new Season { Id = Guid.NewGuid(), CommunityRating = 3f };
        BaseItem[] episodes =
        [
            Episode(changed.Id, "tt0000001", 8f),
            Episode(changed.Id, "tt0000002", 6f),
            Episode(changed.Id, "tt0000003", 5f),
            Episode(unchanged.Id, "tt0000001", 8f),
            Episode(ineligible.Id, "tt0000003", 5f),
            Episode(Guid.NewGuid(), "tt0000001", 8f),
            Episode(Guid.Empty, "tt0000001", 8f)
        ];
        var library = new RecordingLibrary(episodes, [changed, unchanged, ineligible]);
        using var http = new RatingsHttpClientFactory();

        await CreateTask(scope, library, http).ExecuteAsync(new RecordingProgress(), CancellationToken.None);

        Assert.Equal(enabled ? 7f : 4f, changed.CommunityRating);
        Assert.Equal(8f, unchanged.CommunityRating);
        Assert.Equal(3f, ineligible.CommunityRating);
        if (enabled)
        {
            Assert.Same(changed, Assert.Single(library.SingleSaves));
            var seasonQuery = library.Queries[1];
            Assert.Equal(new[] { BaseItemKind.Season }, seasonQuery.IncludeItemTypes);
            Assert.False(seasonQuery.IsVirtualItem);
            Assert.True(seasonQuery.Recursive);
        }
        else
        {
            Assert.Empty(library.SingleSaves);
            Assert.Single(library.Queries);
        }

        Assert.Equal($"{(enabled ? 1 : 0)} ratings updated; 7 skipped; 0 not found.", ReadRun(scope).Summary);
    }

    [Fact]
    public async Task ExecuteAsync_NothingEnabled_RemovesProviderIndexWithoutQueryOrDownload()
    {
        using var scope = new PluginTestScope();
        scope.Configuration.IncludeMovies = false;
        scope.Configuration.IncludeSeries = false;
        scope.Configuration.EnableMetadataProvider = false;
        await ImdbRatingsIndex.CreateSorted([1], [80], [100]).WriteAsync(scope.IndexPath, CancellationToken.None);
        var cache = ImdbRatingsIndexCache.GetShared(scope.IndexPath, NullLogger.Instance);
        Assert.NotNull(await cache.GetIndexAsync(CancellationToken.None));
        var library = new RecordingLibrary([]);
        using var http = new RatingsHttpClientFactory();

        await CreateTask(scope, library, http).ExecuteAsync(new RecordingProgress(), CancellationToken.None);

        Assert.Empty(library.Queries);
        Assert.Equal(0, http.Calls);
        Assert.False(File.Exists(scope.IndexPath));
        Assert.Null(await cache.GetIndexAsync(CancellationToken.None));
        Assert.Equal("Completed", ReadRun(scope).Status);
        Assert.Equal("No eligible library items to update.", ReadRun(scope).Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NoUsableLibraryIds_StillBuildsProviderIndex(bool emptyLibrary)
    {
        using var scope = new PluginTestScope();
        dataset.WriteCache(scope.Temp.Path);
        var library = new RecordingLibrary(emptyLibrary ? [] : [Movie(null, 4f), Movie("   ", 4f)]);
        using var http = new RatingsHttpClientFactory();

        await CreateTask(scope, library, http).ExecuteAsync(new RecordingProgress(), CancellationToken.None);

        var index = await ImdbRatingsIndex.TryLoadAsync(scope.IndexPath, CancellationToken.None);
        Assert.NotNull(index);
        Assert.Equal(500_000, index.Count);
        Assert.Empty(library.SingleSaves);
        Assert.Empty(library.BatchSaves);
        Assert.Equal(emptyLibrary ? "No eligible library items to update." : "No valid IMDb IDs to update.", ReadRun(scope).Summary);
        Assert.Equal("Completed", ReadRun(scope).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_InvalidCache_RetriesOnceAndReportsOutcome(bool validDownload)
    {
        using var scope = new PluginTestScope();
        var cacheDirectory = Directory.CreateDirectory(scope.Temp.PathFor("imdb-ratings-cache"));
        var cachePath = Path.Join(cacheDirectory.FullName, "title.ratings.tsv");
        await File.WriteAllTextAsync(cachePath, "invalid cached data", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(cachePath + ".tmp", "stale partial download", TestContext.Current.CancellationToken);
        var movie = Movie("tt0000001", 4f);
        var library = new RecordingLibrary([movie]);
        using var http = new RatingsHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(validDownload ? dataset.Compressed : RatingsDataset.Compress("invalid downloaded data"u8.ToArray()))
        });
        var task = CreateTask(scope, library, http);

        if (validDownload)
        {
            await task.ExecuteAsync(new RecordingProgress(), CancellationToken.None);
            Assert.Equal(8f, movie.CommunityRating);
            Assert.Same(movie, Assert.Single(library.SingleSaves));
            Assert.Equal("Warning", ReadRun(scope).Status);
            Assert.True(File.Exists(scope.IndexPath));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => task.ExecuteAsync(new RecordingProgress(), CancellationToken.None));
            Assert.Equal(4f, movie.CommunityRating);
            Assert.Empty(library.SingleSaves);
            Assert.Equal("Failed", ReadRun(scope).Status);
            Assert.False(File.Exists(scope.IndexPath));
        }

        Assert.Equal(1, http.Calls);
        Assert.Contains(ReadRun(scope).Warnings, warning => warning.Contains("Ratings data was invalid; retried download."));
        Assert.False(File.Exists(cachePath + ".tmp"));
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedHttpTimeout_IsFailedRatherThanCancelled()
    {
        using var scope = new PluginTestScope();
        var library = new RecordingLibrary([Movie("tt0000001", 4f)]);
        using var http = new RatingsHttpClientFactory(_ => throw new TaskCanceledException("Simulated HTTP timeout"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateTask(scope, library, http).ExecuteAsync(new RecordingProgress(), CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(2, http.Calls);
        Assert.Equal("Failed", ReadRun(scope).Status);
        Assert.Empty(library.SingleSaves);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledDuringEmptyLibraryIndexBuild_PreservesOldIndex()
    {
        using var scope = new PluginTestScope();
        dataset.WriteCache(scope.Temp.Path);
        await ImdbRatingsIndex.CreateSorted([1], [40], [100]).WriteAsync(scope.IndexPath, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress(value =>
        {
            if (value == 0)
            {
                cancellation.Cancel();
            }
        });
        using var http = new RatingsHttpClientFactory();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateTask(scope, new RecordingLibrary([]), http).ExecuteAsync(progress, cancellation.Token));

        Assert.Equal("Cancelled", ReadRun(scope).Status);
        var index = await ImdbRatingsIndex.TryLoadAsync(scope.IndexPath, CancellationToken.None);
        Assert.NotNull(index);
        Assert.True(index.TryGetRating("tt0000001", 1, out var rating, out _));
        Assert.Equal(4f, rating);
        Assert.DoesNotContain(100, progress.Values);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderDisabledDuringRefresh_SavesRatingsWithoutPublishingIndex()
    {
        using var scope = new PluginTestScope();
        dataset.WriteCache(scope.Temp.Path);
        var movie = Movie("tt0000001", 4f);
        var library = new RecordingLibrary([movie]);
        using var http = new RatingsHttpClientFactory();
        var progress = new RecordingProgress(value =>
        {
            if (value >= 90)
            {
                scope.Configuration.EnableMetadataProvider = false;
            }
        });

        await CreateTask(scope, library, http).ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(8f, movie.CommunityRating);
        Assert.Same(movie, Assert.Single(library.SingleSaves));
        Assert.False(File.Exists(scope.IndexPath));
        Assert.Equal("Completed", ReadRun(scope).Status);
    }

    private static RefreshImdbRatingsTask CreateTask(
        PluginTestScope scope, RecordingLibrary library, IHttpClientFactory http, ILogger<RefreshImdbRatingsTask>? logger = null)
    {
        var service = library.Service;
        BaseItem.LibraryManager = service;
        return new(service, http, logger ?? NullLogger<RefreshImdbRatingsTask>.Instance, NullLoggerFactory.Instance, scope.Paths);
    }

    private static RefreshRun ReadRun(PluginTestScope scope)
        => Assert.Single(new RefreshRunHistory(scope.Temp.Path, NullLogger.Instance).Read());

    private static Movie Movie(string? imdbId, float? rating, Folder? parent = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie", CommunityRating = rating };
        if (parent is not null)
        {
            movie.SetParent(parent);
        }
        if (imdbId is not null)
        {
            movie.ProviderIds["Imdb"] = imdbId;
        }
        return movie;
    }

    private static Episode Episode(Guid seasonId, string imdbId, float rating)
    {
        var episode = new Episode { Id = Guid.NewGuid(), SeasonId = seasonId, CommunityRating = rating };
        episode.ProviderIds["Imdb"] = imdbId;
        return episode;
    }

    private sealed class RecordingProgress(Action<double>? onReport = null) : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            Values.Add(value);
            onReport?.Invoke(value);
        }
    }

    private sealed class RecordingLibrary(
        IReadOnlyList<BaseItem> items, IReadOnlyList<BaseItem>? seasons = null, IReadOnlyList<BaseItem>? parents = null)
    {
        public List<InternalItemsQuery> Queries { get; } = new();

        public List<BaseItem> SingleSaves { get; } = new();

        public List<(IReadOnlyList<BaseItem> Items, BaseItem Parent)> BatchSaves { get; } = new();

        public ILibraryManager Service => TestServiceProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == nameof(ILibraryManager.GetItemById))
            {
                return parents?.Single(item => item.Id == (Guid)args![0]!);
            }

            if (method.Name == nameof(ILibraryManager.GetItemList))
            {
                var query = (InternalItemsQuery)args![0]!;
                Queries.Add(query);
                return query.IncludeItemTypes.Contains(BaseItemKind.Season) ? seasons! : items;
            }

            Assert.Equal(ItemUpdateType.MetadataEdit, args![2]);
            if (method.Name == nameof(ILibraryManager.UpdateItemAsync))
            {
                SingleSaves.Add((BaseItem)args[0]!);
                return Task.CompletedTask;
            }

            if (method.Name == nameof(ILibraryManager.UpdateItemsAsync))
            {
                BatchSaves.Add(((IReadOnlyList<BaseItem>)args[0]!, (BaseItem)args[1]!));
                return Task.CompletedTask;
            }

            throw new InvalidOperationException($"Unexpected library call: {method.Name}");
        });
    }

    private sealed class RatingsHttpClientFactory(Func<int, HttpResponseMessage>? respond = null) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond?.Invoke(Calls) ?? throw new InvalidOperationException("Unexpected download"));
        }
    }
}

public sealed class RatingsDataset : IDisposable
{
    private readonly TempDirectory _temp = new();

    public RatingsDataset()
    {
        using (var writer = new StreamWriter(_temp.PathFor("ratings.tsv")))
        {
            writer.WriteLine("tconst\taverageRating\tnumVotes");
            writer.WriteLine("tt0000001\t8.0\t100");
            writer.WriteLine("tt0000002\t6.0\t200");
            writer.WriteLine("tt0000003\t5.0\t99");
            for (int i = 4; i <= 500_000; i++)
            {
                writer.WriteLine($"tt{i:0000000}\t7.5\t100");
            }
        }

        Compressed = Compress(File.ReadAllBytes(_temp.PathFor("ratings.tsv")));
    }

    public byte[] Compressed { get; }

    public void WriteCache(string dataPath)
    {
        var directory = Directory.CreateDirectory(Path.Join(dataPath, "imdb-ratings-cache"));
        File.Copy(_temp.PathFor("ratings.tsv"), Path.Join(directory.FullName, "title.ratings.tsv"));
    }

    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    public void Dispose() => _temp.Dispose();
}
