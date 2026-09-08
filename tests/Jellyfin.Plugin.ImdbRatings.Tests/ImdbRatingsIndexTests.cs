using Jellyfin.Plugin.ImdbRatings.Providers;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class ImdbRatingsIndexTests
{
    private static ImdbRatingsIndex CreateIndex(params (uint Id, float Rating, uint Votes)[] rows)
    {
        var ids = new uint[rows.Length];
        var ratings = new byte[rows.Length];
        var votes = new uint[rows.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            ids[i] = rows[i].Id;
            ratings[i] = ImdbRatingsIndex.EncodeRating(rows[i].Rating);
            votes[i] = rows[i].Votes;
        }

        return ImdbRatingsIndex.CreateSorted(ids, ratings, votes);
    }

    [Theory]
    [InlineData(1.0f, 10)]
    [InlineData(7.3f, 73)]
    [InlineData(9.9f, 99)]
    [InlineData(10.0f, 100)]
    public void EncodeRating_SingleDecimalValues_RoundTripExactly(float rating, byte expected)
    {
        Assert.Equal(expected, ImdbRatingsIndex.EncodeRating(rating));
    }

    [Fact]
    public void EncodeRating_RoundsHalfAwayFromZero()
    {
        // 7.25 is not a value IMDb publishes, but the encoder must not silently truncate.
        Assert.Equal(73, ImdbRatingsIndex.EncodeRating(7.25f));
    }

    [Theory]
    [InlineData("tt0000001", 1u)]
    [InlineData("tt9916880", 9916880u)]
    [InlineData("tt43858878", 43858878u)]
    public void TryParseImdbId_ValidIds_ReturnsNumericForm(string imdbId, uint expected)
    {
        Assert.True(ImdbRatingsIndex.TryParseImdbId(imdbId, out var numericId));
        Assert.Equal(expected, numericId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tt")]
    [InlineData("nm0000001")]
    [InlineData("tt00x0001")]
    [InlineData("0000001")]
    [InlineData("tt99999999999")]
    [InlineData("tt18446744073709551616")]
    public void TryParseImdbId_InvalidOrOversizedIds_ReturnsFalse(string? imdbId)
    {
        Assert.False(ImdbRatingsIndex.TryParseImdbId(imdbId, out _));
    }

    [Fact]
    public void CreateSorted_UnsortedInput_KeepsRowsTogether()
    {
        // The IMDb flat file is byte-sorted, not numerically sorted: tt10001002 precedes tt9916850 in the
        // source. CreateSorted must reorder the keys and carry each row's rating and votes along with them.
        var index = CreateIndex(
            (10001002u, 6.1f, 500),
            (9916850u, 8.4f, 1200),
            (1u, 5.7f, 2224));

        Assert.Equal(3, index.Count);

        Assert.True(index.TryGetRating("tt10001002", 1, out var first, out var firstVotes));
        Assert.Equal(6.1f, first, 3);
        Assert.Equal(500, firstVotes);

        Assert.True(index.TryGetRating("tt9916850", 1, out var second, out var secondVotes));
        Assert.Equal(8.4f, second, 3);
        Assert.Equal(1200, secondVotes);

        Assert.True(index.TryGetRating("tt0000001", 1, out var third, out var thirdVotes));
        Assert.Equal(5.7f, third, 3);
        Assert.Equal(2224, thirdVotes);
    }

    [Fact]
    public void TryGetRating_MissingId_ReturnsFalse()
    {
        var index = CreateIndex((10u, 7.0f, 100), (20u, 8.0f, 200), (30u, 9.0f, 300));

        Assert.False(index.TryGetRating("tt0000015", 1, out _, out _));
    }

    [Fact]
    public void TryGetRating_FirstAndLastEntries_AreFound()
    {
        var index = CreateIndex((10u, 7.0f, 100), (20u, 8.0f, 200), (30u, 9.0f, 300));

        Assert.True(index.TryGetRating("tt0000010", 1, out var first, out _));
        Assert.Equal(7.0f, first, 3);

        Assert.True(index.TryGetRating("tt0000030", 1, out var last, out _));
        Assert.Equal(9.0f, last, 3);
    }

    [Fact]
    public void TryGetRating_BelowMinimumVotes_ReturnsFalse()
    {
        var index = CreateIndex((10u, 9.4f, 11));

        Assert.False(index.TryGetRating("tt0000010", 1000, out _, out _));
        Assert.True(index.TryGetRating("tt0000010", 11, out var rating, out var votes));
        Assert.Equal(9.4f, rating, 3);
        Assert.Equal(11, votes);
    }

    [Fact]
    public void TryGetRating_MalformedId_ReturnsFalse()
    {
        var index = CreateIndex((10u, 7.0f, 100));

        Assert.False(index.TryGetRating("not-an-id", 1, out _, out _));
        Assert.False(index.TryGetRating(null, 1, out _, out _));
    }

    [Fact]
    public async Task WriteAsync_ThenTryLoadAsync_RoundTripsAllRows()
    {
        var directory = CreateTempDirectory();
        try
        {
            var indexPath = Path.Combine(directory, "title.ratings.idx");
            var index = CreateIndex(
                (1u, 5.7f, 2224),
                (43858878u, 10.0f, 3221521),
                (9916880u, 1.0f, 5));

            await index.WriteAsync(indexPath, CancellationToken.None);

            var loaded = await ImdbRatingsIndex.TryLoadAsync(indexPath, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(3, loaded!.Count);

            Assert.True(loaded.TryGetRating("tt0000001", 1, out var a, out var aVotes));
            Assert.Equal(5.7f, a, 3);
            Assert.Equal(2224, aVotes);

            Assert.True(loaded.TryGetRating("tt43858878", 1, out var b, out var bVotes));
            Assert.Equal(10.0f, b, 3);
            Assert.Equal(3221521, bVotes);

            Assert.True(loaded.TryGetRating("tt9916880", 1, out var c, out var cVotes));
            Assert.Equal(1.0f, c, 3);
            Assert.Equal(5, cVotes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_ReplacesExistingIndexAndLeavesNoTempFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var indexPath = Path.Combine(directory, "title.ratings.idx");

            await CreateIndex((1u, 5.0f, 10)).WriteAsync(indexPath, CancellationToken.None);
            await CreateIndex((1u, 6.0f, 20), (2u, 7.0f, 30)).WriteAsync(indexPath, CancellationToken.None);

            var loaded = await ImdbRatingsIndex.TryLoadAsync(indexPath, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Count);
            Assert.True(loaded.TryGetRating("tt0000001", 1, out var rating, out _));
            Assert.Equal(6.0f, rating, 3);
            Assert.False(File.Exists(indexPath + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_ReplacesExistingIndexWhileProductionLoaderHoldsOldFileOpen()
    {
        var directory = CreateTempDirectory();
        try
        {
            var indexPath = Path.Combine(directory, "title.ratings.idx");
            await CreateIndex((1u, 5.0f, 10)).WriteAsync(indexPath, CancellationToken.None);

            var readerOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldLoad = ImdbRatingsIndex.TryLoadAsync(
                indexPath,
                CancellationToken.None,
                async cancellationToken =>
                {
                    readerOpened.SetResult();
                    await releaseReader.Task.WaitAsync(cancellationToken);
                });

            await readerOpened.Task;

            try
            {
                await CreateIndex((1u, 9.0f, 20)).WriteAsync(indexPath, CancellationToken.None);
            }
            finally
            {
                releaseReader.TrySetResult();
            }

            var oldIndex = await oldLoad;
            Assert.NotNull(oldIndex);
            Assert.True(oldIndex!.TryGetRating("tt0000001", 0, out var oldRating, out _));
            Assert.Equal(5.0f, oldRating, 3);

            var replacement = await ImdbRatingsIndex.TryLoadAsync(indexPath, CancellationToken.None);
            Assert.NotNull(replacement);
            Assert.True(replacement!.TryGetRating("tt0000001", 0, out var rating, out _));
            Assert.Equal(9.0f, rating, 3);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryLoadAsync_MissingFile_ReturnsNull()
    {
        var directory = CreateTempDirectory();
        try
        {
            var result = await ImdbRatingsIndex.TryLoadAsync(
                Path.Combine(directory, "absent.idx"),
                CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryLoadAsync_TruncatedFile_ReturnsNull()
    {
        var directory = CreateTempDirectory();
        try
        {
            var indexPath = Path.Combine(directory, "title.ratings.idx");
            await CreateIndex((1u, 5.0f, 10), (2u, 6.0f, 20)).WriteAsync(indexPath, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(indexPath);
            await File.WriteAllBytesAsync(indexPath, bytes[..(bytes.Length - 4)]);

            Assert.Null(await ImdbRatingsIndex.TryLoadAsync(indexPath, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryLoadAsync_WrongMagicOrVersion_ReturnsNull()
    {
        var directory = CreateTempDirectory();
        try
        {
            var indexPath = Path.Combine(directory, "title.ratings.idx");
            await CreateIndex((1u, 5.0f, 10)).WriteAsync(indexPath, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(indexPath);

            var wrongMagic = bytes.ToArray();
            wrongMagic[0] = (byte)'X';
            await File.WriteAllBytesAsync(indexPath, wrongMagic);
            Assert.Null(await ImdbRatingsIndex.TryLoadAsync(indexPath, CancellationToken.None));

            var wrongVersion = bytes.ToArray();
            wrongVersion[8] = (byte)(ImdbRatingsIndex.FormatVersion + 1);
            await File.WriteAllBytesAsync(indexPath, wrongVersion);
            Assert.Null(await ImdbRatingsIndex.TryLoadAsync(indexPath, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetIndexPath_SitsBesideTheRatingsCache()
    {
        var path = ImdbRatingsIndex.GetIndexPath(Path.Combine("data", "root"));

        Assert.Equal(
            Path.Combine("data", "root", "imdb-ratings-cache", "title.ratings.idx"),
            path);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "imdb-ratings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
