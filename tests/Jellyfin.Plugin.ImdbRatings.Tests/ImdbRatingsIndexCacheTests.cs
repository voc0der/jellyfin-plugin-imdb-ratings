using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class ImdbRatingsIndexCacheTests
{
    [Fact]
    public async Task GetIndexAsync_NoIndexOnDisk_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var cache = new ImdbRatingsIndexCache(temp.PathFor("title.ratings.idx"), NullLogger.Instance);

        Assert.Null(await cache.GetIndexAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetIndexAsync_AbsentIndex_IsNegativelyCachedUntilInvalidated()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        var cache = new ImdbRatingsIndexCache(indexPath, NullLogger.Instance);

        Assert.Null(await cache.GetIndexAsync(CancellationToken.None));

        await WriteIndexAsync(indexPath);

        // Still null: the miss is cached for the recheck interval, so a scan over a server with no index
        // does not stat the file once per item.
        Assert.Null(await cache.GetIndexAsync(CancellationToken.None));

        cache.Invalidate();

        var loaded = await cache.GetIndexAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
    }

    [Fact]
    public async Task GetIndexAsync_CorruptIndex_ReturnsNullAndDoesNotThrow()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        await File.WriteAllBytesAsync(indexPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var cache = new ImdbRatingsIndexCache(indexPath, NullLogger.Instance);

        Assert.Null(await cache.GetIndexAsync(CancellationToken.None));
        Assert.Null(await cache.GetIndexAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetIndexAsync_LoadedIndex_IsServedFromMemoryAfterFileRemoval()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        await WriteIndexAsync(indexPath);

        var cache = new ImdbRatingsIndexCache(indexPath, NullLogger.Instance);
        Assert.NotNull(await cache.GetIndexAsync(CancellationToken.None));

        File.Delete(indexPath);

        // Within the recheck interval the loaded copy keeps serving rather than re-statting per item.
        Assert.NotNull(await cache.GetIndexAsync(CancellationToken.None));

        cache.Invalidate();
        Assert.Null(await cache.GetIndexAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetIndexAsync_ConcurrentInitialCallers_WaitForOneLoad()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        await WriteIndexAsync(indexPath);

        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;
        var cache = new ImdbRatingsIndexCache(
            indexPath,
            NullLogger.Instance,
            async (path, cancellationToken) =>
            {
                Interlocked.Increment(ref loadCount);
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return await ImdbRatingsIndex.TryLoadAsync(path, cancellationToken);
            });

        var first = cache.GetIndexAsync(CancellationToken.None);
        await loadStarted.Task;

        var second = cache.GetIndexAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        releaseLoad.SetResult();

        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task GetIndexAsync_CanceledLoad_DoesNotPoisonObservedWriteTime()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        await WriteIndexAsync(indexPath);

        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;
        var cache = new ImdbRatingsIndexCache(
            indexPath,
            NullLogger.Instance,
            async (path, cancellationToken) =>
            {
                if (Interlocked.Increment(ref loadCount) == 1)
                {
                    firstLoadStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return await ImdbRatingsIndex.TryLoadAsync(path, cancellationToken);
            });

        using var cancellation = new CancellationTokenSource();
        var canceledLoad = cache.GetIndexAsync(cancellation.Token);
        await firstLoadStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledLoad);

        var retried = await cache.GetIndexAsync(CancellationToken.None);
        Assert.NotNull(retried);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task Invalidate_DuringLoad_RetriesCurrentGenerationWithoutRepopulatingOldOne()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        await WriteIndexAsync(indexPath);

        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;
        var cache = new ImdbRatingsIndexCache(
            indexPath,
            NullLogger.Instance,
            async (path, cancellationToken) =>
            {
                if (Interlocked.Increment(ref loadCount) == 1)
                {
                    firstLoadStarted.SetResult();
                    await releaseFirstLoad.Task.WaitAsync(cancellationToken);
                }

                return await ImdbRatingsIndex.TryLoadAsync(path, cancellationToken);
            });

        var invalidatedLoad = cache.GetIndexAsync(CancellationToken.None);
        await firstLoadStarted.Task;

        cache.Invalidate();
        releaseFirstLoad.SetResult();

        Assert.NotNull(await invalidatedLoad);
        Assert.NotNull(await cache.GetIndexAsync(CancellationToken.None));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task GetIndexAsync_TransientIoFailure_PreservesLastGoodIndexAndRetriesChangedFile()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        await WriteIndexAsync(indexPath, 5.7f);

        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        int loadCount = 0;
        var cache = new ImdbRatingsIndexCache(
            indexPath,
            NullLogger.Instance,
            async (path, cancellationToken) =>
            {
                if (Interlocked.Increment(ref loadCount) == 2)
                {
                    throw new IOException("Simulated transient read failure.");
                }

                return await ImdbRatingsIndex.TryLoadAsync(path, cancellationToken);
            },
            timeProvider);

        var first = await cache.GetIndexAsync(CancellationToken.None);
        AssertRating(first, 5.7f);

        var oldWriteTime = File.GetLastWriteTimeUtc(indexPath);
        await WriteIndexAsync(indexPath, 9.1f);
        File.SetLastWriteTimeUtc(indexPath, oldWriteTime.AddSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(61));

        var fallback = await cache.GetIndexAsync(CancellationToken.None);
        AssertRating(fallback, 5.7f);

        timeProvider.Advance(TimeSpan.FromSeconds(61));
        var retried = await cache.GetIndexAsync(CancellationToken.None);
        AssertRating(retried, 9.1f);
        Assert.Equal(3, loadCount);
    }

    [Fact]
    public async Task GetIndexAsync_TransientStatFailure_PreservesLastGoodIndexAndRetriesChangedFile()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");
        await WriteIndexAsync(indexPath, 5.7f);

        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        int statCount = 0;
        int loadCount = 0;
        var cache = new ImdbRatingsIndexCache(
            indexPath,
            NullLogger.Instance,
            async (path, cancellationToken) =>
            {
                Interlocked.Increment(ref loadCount);
                return await ImdbRatingsIndex.TryLoadAsync(path, cancellationToken);
            },
            timeProvider,
            path =>
            {
                if (Interlocked.Increment(ref statCount) == 2)
                {
                    throw new IOException("Simulated transient stat failure.");
                }

                return File.GetLastWriteTimeUtc(path);
            });

        var first = await cache.GetIndexAsync(CancellationToken.None);
        AssertRating(first, 5.7f);

        var oldWriteTime = File.GetLastWriteTimeUtc(indexPath);
        await WriteIndexAsync(indexPath, 9.1f);
        File.SetLastWriteTimeUtc(indexPath, oldWriteTime.AddSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(61));

        var fallback = await cache.GetIndexAsync(CancellationToken.None);
        AssertRating(fallback, 5.7f);

        timeProvider.Advance(TimeSpan.FromSeconds(61));
        var retried = await cache.GetIndexAsync(CancellationToken.None);
        AssertRating(retried, 9.1f);
        Assert.Equal(3, statCount);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void GetShared_SamePath_ReturnsOneInstance()
    {
        using var temp = new TempDirectory();
        var indexPath = temp.PathFor("title.ratings.idx");

        var first = ImdbRatingsIndexCache.GetShared(indexPath, NullLogger.Instance);
        var second = ImdbRatingsIndexCache.GetShared(indexPath, NullLogger.Instance);

        Assert.Same(first, second);
    }

    private static async Task WriteIndexAsync(string indexPath, float firstRating = 5.7f)
    {
        var index = ImdbRatingsIndex.CreateSorted(
            new uint[] { 2, 1 },
            new byte[] { 80, ImdbRatingsIndex.EncodeRating(firstRating) },
            new uint[] { 200, 100 });

        await index.WriteAsync(indexPath, CancellationToken.None);
    }

    private static void AssertRating(ImdbRatingsIndex? index, float expected)
    {
        Assert.NotNull(index);
        Assert.True(index!.TryGetRating("tt0000001", 0, out var rating, out _));
        Assert.Equal(expected, rating, 3);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
        }
    }
}
