using System.Globalization;
using System.Text;
using Jellyfin.Plugin.ImdbRatings.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// Covers the parser's remaining validation gates and its unfiltered entry point. These are the checks that
/// stop a corrupt download from replacing good ratings with garbage.
/// </summary>
public class ImdbRatingsParserValidationTests
{
    // Matches ImdbRatingsParser.MinExpectedRows.
    private const int MinExpectedRows = 500_000;

    [Fact]
    public async Task ParseAsync_WellFormedFile_ReturnsEveryRow()
    {
        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await WriteRatingsFileAsync(path, MinExpectedRows, malformedRowCount: 0);

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
        var ratings = await parser.ParseAsync(path, CancellationToken.None);

        Assert.Equal(MinExpectedRows, ratings.Count);
        Assert.True(ratings.TryGetValue("tt0000001", out var first));
        Assert.Equal(ExpectedRating(1), first.Rating, 3);
        Assert.Equal(ExpectedVotes(1), first.Votes);
    }

    [Fact]
    public async Task ParseAsync_ParseErrorsAboveOnePercent_RejectsTheFileAsCorrupt()
    {
        // Enough valid rows to clear the truncation gate, so the error-ratio gate is the one under test.
        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await WriteRatingsFileAsync(path, MinExpectedRows, malformedRowCount: 10_000);

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(path, CancellationToken.None));

        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_ParseErrorsBelowOnePercent_AreToleratedAndSkipped()
    {
        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await WriteRatingsFileAsync(path, MinExpectedRows, malformedRowCount: 100);

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
        var ratings = await parser.ParseAsync(path, CancellationToken.None);

        Assert.Equal(MinExpectedRows, ratings.Count);
    }

    [Fact]
    public async Task ParseAsync_HeaderOnly_ReportsNoDataRows()
    {
        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await File.WriteAllTextAsync(path, "tconst\taverageRating\tnumVotes\n");

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(path, CancellationToken.None));

        Assert.Contains("no valid data rows", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_EmptyFile_ReportsMissingHeader()
    {
        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await File.WriteAllTextAsync(path, string.Empty);

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(path, CancellationToken.None));

        Assert.Contains("empty file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_RowLongerThanTheReadBuffer_IsStillParsed()
    {
        // The scanner grows its buffer when a single line does not fit in the 128 KB read buffer. Real IMDb
        // rows are tiny, so nothing else exercises that path.
        const int oversizedIdDigits = 200_000;
        var longId = "tt" + new string('1', oversizedIdDigits);

        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await WriteRatingsFileAsync(path, MinExpectedRows, malformedRowCount: 0, extraRow: $"{longId}\t6.6\t4242");

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
        var ratings = await parser.ParseAsync(path, CancellationToken.None);

        Assert.Equal(MinExpectedRows + 1, ratings.Count);
        Assert.True(ratings.TryGetValue(longId, out var oversized));
        Assert.Equal(6.6f, oversized.Rating, 3);
        Assert.Equal(4242, oversized.Votes);
    }

    [Fact]
    public async Task ParseFilteredAsync_NullIncludeIds_Throws()
    {
        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await File.WriteAllTextAsync(path, "tconst\taverageRating\tnumVotes\n");

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => parser.ParseFilteredAsync(path, null!, CancellationToken.None));
    }

    [Fact]
    public async Task ParseAsync_Canceled_Throws()
    {
        using var temp = new TempDirectory();
        var path = temp.PathFor("title.ratings.tsv");
        await WriteRatingsFileAsync(path, 1000, malformedRowCount: 0);

        var parser = new ImdbRatingsParser(NullLogger<ImdbRatingsParser>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parser.ParseAsync(path, cts.Token));
    }

    private static async Task WriteRatingsFileAsync(
        string path,
        int validRowCount,
        int malformedRowCount,
        string? extraRow = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII);
        writer.NewLine = "\n";

        await writer.WriteLineAsync("tconst\taverageRating\tnumVotes");

        for (int i = 1; i <= validRowCount; i++)
        {
            await writer.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"tt{i:0000000}\t{ExpectedRating(i):0.0}\t{ExpectedVotes(i)}"));
        }

        // Rows the field parser rejects: a missing column and an unparseable rating.
        for (int i = 0; i < malformedRowCount; i++)
        {
            await writer.WriteLineAsync(i % 2 == 0
                ? $"tt9{i:000000}\tnot-a-rating\t100"
                : $"tt8{i:000000}\tmissing-second-tab");
        }

        if (extraRow is not null)
        {
            await writer.WriteLineAsync(extraRow);
        }
    }

    private static float ExpectedRating(int index) => ((index % 90) + 10) / 10f;

    private static int ExpectedVotes(int index) => 1000 + index;
}
