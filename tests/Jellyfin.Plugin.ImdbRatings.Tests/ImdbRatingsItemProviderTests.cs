using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// Covers the scan-time provider's decision ladder. Every branch here is a reason to leave an item
/// untouched, so a regression shows up as ratings silently applied (or silently not applied) during a scan.
/// </summary>
public class ImdbRatingsItemProviderTests
{
    private const string KnownId = "tt0000001";
    private const float KnownRating = 8.4f;
    private const int KnownVotes = 500;

    [Fact]
    public void Apply_RatedItemWithNoExistingRating_SetsRatingAndReportsDownload()
    {
        var movie = MovieWith(KnownId, communityRating: null);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.MetadataDownload, result);
        Assert.Equal(KnownRating, movie.CommunityRating!.Value, 3);
    }

    [Fact]
    public void Apply_ProviderDisabled_LeavesItemUntouched()
    {
        var movie = MovieWith(KnownId, communityRating: null);
        var config = Config();
        config.EnableMetadataProvider = false;

        var result = ImdbRatingsItemProvider.Apply(movie, config, typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Null(movie.CommunityRating);
    }

    [Fact]
    public void Apply_ItemTypeDisabled_LeavesItemUntouched()
    {
        var movie = MovieWith(KnownId, communityRating: null);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: false, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Null(movie.CommunityRating);
    }

    [Fact]
    public void Apply_NoIndexAvailable_LeavesItemUntouched()
    {
        var movie = MovieWith(KnownId, communityRating: null);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: true, index: null, NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Null(movie.CommunityRating);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_MissingOrBlankImdbId_LeavesItemUntouched(string? imdbId)
    {
        var movie = MovieWith(imdbId, communityRating: null);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Null(movie.CommunityRating);
    }

    [Fact]
    public void Apply_ImdbIdNotInIndex_LeavesItemUntouched()
    {
        var movie = MovieWith("tt0000999", communityRating: null);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Null(movie.CommunityRating);
    }

    [Fact]
    public void Apply_VotesBelowMinimum_LeavesItemUntouched()
    {
        var movie = MovieWith(KnownId, communityRating: null);
        var config = Config();
        config.MinimumVotes = KnownVotes + 1;

        var result = ImdbRatingsItemProvider.Apply(movie, config, typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Null(movie.CommunityRating);
    }

    [Fact]
    public void Apply_VotesExactlyAtMinimum_AppliesRating()
    {
        var movie = MovieWith(KnownId, communityRating: null);
        var config = Config();
        config.MinimumVotes = KnownVotes;

        var result = ImdbRatingsItemProvider.Apply(movie, config, typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.MetadataDownload, result);
    }

    [Fact]
    public void Apply_RatingAlreadyMatches_ReportsNoUpdate()
    {
        var movie = MovieWith(KnownId, communityRating: KnownRating);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Equal(KnownRating, movie.CommunityRating!.Value, 3);
    }

    [Fact]
    public void Apply_RatingDiffersWithinTolerance_ReportsNoUpdateAndDoesNotRewrite()
    {
        // Below the shared tolerance: treating this as a change would make the provider and the scheduled
        // task disagree and rewrite the item on every scan.
        var almost = KnownRating - (RatingComparison.Tolerance / 2f);
        var movie = MovieWith(KnownId, communityRating: almost);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.None, result);
        Assert.Equal(almost, movie.CommunityRating!.Value, 4);
    }

    [Fact]
    public void Apply_RatingDiffersByMoreThanTolerance_AppliesRating()
    {
        var movie = MovieWith(KnownId, communityRating: KnownRating - 0.1f);

        var result = ImdbRatingsItemProvider.Apply(movie, Config(), typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.MetadataDownload, result);
        Assert.Equal(KnownRating, movie.CommunityRating!.Value, 3);
    }

    [Fact]
    public void Apply_SeriesAndEpisode_AreRatedLikeMovies()
    {
        var series = new Series { Name = "S" };
        series.SetProviderId(MetadataProvider.Imdb, KnownId);
        var episode = new Episode { Name = "E" };
        episode.SetProviderId(MetadataProvider.Imdb, KnownId);

        Assert.Equal(
            ItemUpdateType.MetadataDownload,
            ImdbRatingsItemProvider.Apply(series, Config(), typeEnabled: true, Index(), NullLogger.Instance));
        Assert.Equal(
            ItemUpdateType.MetadataDownload,
            ImdbRatingsItemProvider.Apply(episode, Config(), typeEnabled: true, Index(), NullLogger.Instance));
    }

    [Fact]
    public void Apply_DebugLoggingEnabled_StillAppliesRating()
    {
        var movie = MovieWith(KnownId, communityRating: null);
        var config = Config();
        config.EnableItemDebugLogging = true;

        var result = ImdbRatingsItemProvider.Apply(movie, config, typeEnabled: true, Index(), NullLogger.Instance);

        Assert.Equal(ItemUpdateType.MetadataDownload, result);
        Assert.Equal(KnownRating, movie.CommunityRating!.Value, 3);
    }

    [Fact]
    public void Apply_NullArguments_Throw()
    {
        var movie = MovieWith(KnownId, communityRating: null);

        Assert.Throws<ArgumentNullException>(
            () => ImdbRatingsItemProvider.Apply(null!, Config(), true, Index(), NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(
            () => ImdbRatingsItemProvider.Apply(movie, null!, true, Index(), NullLogger.Instance));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TypeSelectors_MapEachItemTypeToItsOwnSetting(bool includeMovies, bool includeSeries)
    {
        var config = new PluginConfiguration
        {
            IncludeMovies = includeMovies,
            IncludeSeries = includeSeries
        };

        Assert.Equal(includeMovies, ImdbRatingsItemProvider.IsMovieEnabled(config));
        Assert.Equal(includeSeries, ImdbRatingsItemProvider.IsSeriesEnabled(config));
    }

    private static PluginConfiguration Config()
    {
        return new PluginConfiguration
        {
            EnableMetadataProvider = true,
            IncludeMovies = true,
            IncludeSeries = true,
            MinimumVotes = 1
        };
    }

    private static Movie MovieWith(string? imdbId, float? communityRating)
    {
        var movie = new Movie { Name = "Test", CommunityRating = communityRating };
        if (imdbId is not null)
        {
            // Written straight into the dictionary: SetProviderId rejects blanks, so a blank IMDb ID can only
            // reach the provider from data written by some other path. The guard under test exists for that.
            movie.ProviderIds[MetadataProvider.Imdb.ToString()] = imdbId;
        }

        return movie;
    }

    private static ImdbRatingsIndex Index()
    {
        return ImdbRatingsIndex.CreateSorted(
            new uint[] { 1 },
            new[] { ImdbRatingsIndex.EncodeRating(KnownRating) },
            new uint[] { KnownVotes });
    }
}
