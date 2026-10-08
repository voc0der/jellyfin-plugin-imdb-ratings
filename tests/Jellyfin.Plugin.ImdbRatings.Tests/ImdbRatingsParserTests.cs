using System.Globalization;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class ImdbRatingsParserTests
{
    [Fact]
    public async Task ParseFilteredAsync_ParsesCrLfAndFinalLineWithoutTrailingNewline_AndReturnsOnlyMatches()
    {
        const int rowCount = 500_000;
        var includeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            FormatImdbId(1),
            FormatImdbId(42),
            FormatImdbId(rowCount),
            "tt9999999"
        };

        using var temp = new TempDirectory();
        string path = temp.PathFor("ratings.tsv");
        await WriteRatingsFileAsync(path, rowCount, useCrLf: true, omitFinalNewline: true);

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
        var ratings = await parser.ParseFilteredAsync(path, includeIds, CancellationToken.None);

        Assert.Equal(3, ratings.Count);

        AssertRatingRow(ratings, 1);
        AssertRatingRow(ratings, 42);
        AssertRatingRow(ratings, rowCount);
        Assert.False(ratings.ContainsKey("tt9999999"));
    }

    [Fact]
    public async Task ParseFilteredAsync_ThrowsOnInvalidHeader()
    {
        using var temp = new TempDirectory();
        string path = temp.PathFor("ratings.tsv");
        await File.WriteAllTextAsync(
            path,
            "bad_header\n" + "tt0000001\t7.1\t100\n",
            TestContext.Current.CancellationToken);

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            parser.ParseFilteredAsync(path, new HashSet<string>(StringComparer.Ordinal) { "tt0000001" }, CancellationToken.None));

        Assert.Contains("invalid or missing header", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseFilteredAsync_ThrowsOnTruncatedFile_EvenWhenMatchesExist()
    {
        using var temp = new TempDirectory();
        string path = temp.PathFor("ratings.tsv");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteLineAsync("tconst\taverageRating\tnumVotes");
            await writer.WriteLineAsync("tt0000001\t7.5\t1234");
            await writer.WriteAsync("tt0000002\t8.1\t5678");
        }

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
        var includeIds = new HashSet<string>(StringComparer.Ordinal) { "tt0000001", "tt0000002" };

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            parser.ParseFilteredAsync(path, includeIds, CancellationToken.None));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteRatingsFileAsync(string path, int rowCount, bool useCrLf, bool omitFinalNewline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string newline = useCrLf ? "\r\n" : "\n";

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream);
        writer.NewLine = newline;

        await writer.WriteLineAsync("tconst\taverageRating\tnumVotes");

        for (int i = 1; i <= rowCount; i++)
        {
            string row = BuildRow(i);
            bool isLast = i == rowCount;

            if (isLast && omitFinalNewline)
            {
                await writer.WriteAsync(row);
            }
            else
            {
                await writer.WriteLineAsync(row);
            }
        }
    }

    private static string BuildRow(int index)
    {
        string imdbId = FormatImdbId(index);
        float rating = ExpectedRating(index);
        int votes = ExpectedVotes(index);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{imdbId}\t{rating:0.0}\t{votes}");
    }

    private static string FormatImdbId(int index) => $"tt{index:0000000}";

    private static float ExpectedRating(int index) => ((index % 90) + 10) / 10f;

    private static int ExpectedVotes(int index) => 1000 + index;

    private static void AssertRatingRow(Dictionary<string, (float Rating, int Votes)> ratings, int index)
    {
        string imdbId = FormatImdbId(index);
        Assert.True(ratings.TryGetValue(imdbId, out var row), $"Expected row for {imdbId}");
        Assert.InRange(Math.Abs(row.Rating - ExpectedRating(index)), 0f, 0.0001f);
        Assert.Equal(ExpectedVotes(index), row.Votes);
    }
}
