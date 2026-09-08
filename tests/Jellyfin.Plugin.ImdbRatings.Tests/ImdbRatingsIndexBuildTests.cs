using System.Globalization;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class ImdbRatingsIndexBuildTests
{
    // Matches ImdbRatingsParser.MinExpectedRows; the parser rejects anything smaller as truncated.
    private const int MinExpectedRows = 500_000;

    [Fact]
    public async Task BuildIndexAsync_WellFormedFile_IndexesEveryRow()
    {
        string path = CreateTempFilePath();
        try
        {
            await WriteRatingsFileAsync(path, MinExpectedRows, id => $"tt{id:0000000}");

            var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
            var index = await parser.BuildIndexAsync(path, CancellationToken.None);

            Assert.Equal(MinExpectedRows, index.Count);

            Assert.True(index.TryGetRating("tt0000001", 0, out var first, out var firstVotes));
            Assert.Equal(ExpectedRating(1), first, 3);
            Assert.Equal(ExpectedVotes(1), firstVotes);

            Assert.True(index.TryGetRating($"tt{MinExpectedRows:0000000}", 0, out var last, out _));
            Assert.Equal(ExpectedRating(MinExpectedRows), last, 3);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public async Task BuildIndexAsync_WellFormedFileWithUnusableIds_ThrowsRatherThanWritingAnEmptyIndex()
    {
        // Every row frames and parses correctly, so the scan-level validation is satisfied, but no tconst is
        // usable. Without the indexable-row check this would produce a valid, empty index that overwrites a
        // good one on disk.
        string path = CreateTempFilePath();
        try
        {
            await WriteRatingsFileAsync(path, MinExpectedRows, id => $"xx{id:0000000}");

            var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => parser.BuildIndexAsync(path, CancellationToken.None));

            Assert.Contains("indexable rows", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [Fact]
    public async Task BuildIndexAsync_IdsTooWideOrLongForNumericTypes_AreSkipped()
    {
        string path = CreateTempFilePath();
        try
        {
            // One row is beyond uint range and one is 2^64, which used to wrap to zero in the ulong parser.
            // The rest are ordinary. The row count stays above the
            // indexable-row floor so this exercises skipping rather than the truncation guard.
            const int rowCount = MinExpectedRows + 2;
            await WriteRatingsFileAsync(
                path,
                rowCount,
                id => id switch
                {
                    7 => "tt99999999999",
                    8 => "tt18446744073709551616",
                    _ => $"tt{id:0000000}"
                });

            var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
            var index = await parser.BuildIndexAsync(path, CancellationToken.None);

            Assert.Equal(rowCount - 2, index.Count);
            Assert.False(index.TryGetRating("tt99999999999", 0, out _, out _));
            Assert.False(index.TryGetRating("tt18446744073709551616", 0, out _, out _));
            Assert.True(index.TryGetRating("tt0000009", 0, out _, out _));
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    private static async Task WriteRatingsFileAsync(string path, int rowCount, Func<int, string> idFactory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream);
        writer.NewLine = "\n";

        await writer.WriteLineAsync("tconst\taverageRating\tnumVotes");

        for (int i = 1; i <= rowCount; i++)
        {
            await writer.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{idFactory(i)}\t{ExpectedRating(i):0.0}\t{ExpectedVotes(i)}"));
        }
    }

    private static float ExpectedRating(int index) => ((index % 90) + 10) / 10f;

    private static int ExpectedVotes(int index) => 1000 + index;

    private static string CreateTempFilePath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "imdb-ratings-index-build-tests");
        return Path.Combine(dir, $"{Guid.NewGuid():N}.tsv");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for test temp files.
        }
    }
}
