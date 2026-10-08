using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

[Collection("Plugin state")]
public class PluginIntegrationTests
{
    [Fact]
    public void Registration_ExposesStableIdentityAndEmbeddedConfigurationPage()
    {
        using var scope = new PluginTestScope();

        Assert.Same(scope.Plugin, Plugin.Instance);
        Assert.Equal("IMDb Ratings", scope.Plugin.Name);
        Assert.Equal(Guid.Parse("f5a3c7e1-9b2d-4f6a-8e0c-1d3b5a7c9e2f"), scope.Plugin.Id);
        Assert.Contains("CommunityRating", scope.Plugin.Description);
        var page = Assert.Single(scope.Plugin.GetPages());
        Assert.Equal(scope.Plugin.Name, page.Name);
        using var resource = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(resource);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateConfiguration_PersistsSettingsAndClearsIndexOnlyWhenDisabled(bool enabled)
    {
        using var scope = new PluginTestScope();
        await WriteIndexAsync(scope);
        var cache = ImdbRatingsIndexCache.GetShared(scope.IndexPath, NullLogger.Instance);
        var loaded = await cache.GetIndexAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        var configuration = new PluginConfiguration { EnableMetadataProvider = enabled, MinimumVotes = 200 };

        scope.Plugin.UpdateConfiguration(configuration);

        Assert.Same(configuration, scope.Configuration);
        Assert.True(File.Exists(scope.Plugin.ConfigurationFilePath));
        Assert.Contains("<MinimumVotes>200</MinimumVotes>", File.ReadAllText(scope.Plugin.ConfigurationFilePath));
        Assert.Equal(enabled, File.Exists(scope.IndexPath));
        Assert.Same(enabled ? loaded : null, await cache.GetIndexAsync(CancellationToken.None));
    }

    [Fact]
    public void UpdateConfiguration_IndexCleanupFails_StillSavesDisabledSetting()
    {
        using var scope = new PluginTestScope();
        // A directory at the file destination makes File.Delete fail on every supported platform.
        Directory.CreateDirectory(scope.IndexPath);

        scope.Plugin.UpdateConfiguration(new PluginConfiguration { EnableMetadataProvider = false });

        Assert.False(scope.Configuration.EnableMetadataProvider);
        Assert.Contains("<EnableMetadataProvider>false</EnableMetadataProvider>", File.ReadAllText(scope.Plugin.ConfigurationFilePath));
        Assert.True(Directory.Exists(scope.IndexPath));
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("Series")]
    [InlineData("Episode")]
    public async Task FetchAsync_LoadsDiskIndexAndChangesOnlyCommunityRating(string kind)
    {
        using var scope = new PluginTestScope();
        await WriteIndexAsync(scope);
        var provider = new ImdbRatingsItemProvider(scope.Paths, NullLogger<ImdbRatingsItemProvider>.Instance);
        var item = CreateItem(kind);
        item.Overview = "Keep this description";
        item.OfficialRating = "PG";
        item.ProviderIds["Tmdb"] = "123";

        var result = await FetchAsync(provider, item, TestContext.Current.CancellationToken);

        Assert.Equal("IMDb Ratings", provider.Name);
        Assert.Equal(ItemUpdateType.MetadataDownload, result);
        Assert.Equal(8.4f, item.CommunityRating);
        Assert.Equal("Keep this description", item.Overview);
        Assert.Equal("PG", item.OfficialRating);
        Assert.Equal("123", item.ProviderIds["Tmdb"]);
        Assert.Equal("tt0000001", item.ProviderIds["Imdb"]);
        Assert.Equal(ItemUpdateType.None, await FetchAsync(provider, item, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("Series")]
    [InlineData("Episode")]
    public async Task FetchAsync_DisabledItemType_SkipsIndexAndPreservesRating(string kind)
    {
        using var scope = new PluginTestScope();
        scope.Configuration.IncludeMovies = kind != "Movie";
        scope.Configuration.IncludeSeries = kind == "Movie";
        var provider = new ImdbRatingsItemProvider(scope.Paths, NullLogger<ImdbRatingsItemProvider>.Instance);
        var item = CreateItem(kind);

        // A canceled token would fail an index load, so success proves this guard runs first.
        Assert.Equal(ItemUpdateType.None, await FetchAsync(provider, item, new CancellationToken(canceled: true)));
        Assert.Equal(4f, item.CommunityRating);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FetchAsync_MissingImdbId_SkipsIndex(string? imdbId)
    {
        using var scope = new PluginTestScope();
        var provider = new ImdbRatingsItemProvider(scope.Paths, NullLogger<ImdbRatingsItemProvider>.Instance);
        var movie = new Movie { CommunityRating = 4f };
        if (imdbId is not null)
        {
            movie.ProviderIds["Imdb"] = imdbId;
        }

        Assert.Equal(ItemUpdateType.None, await FetchAsync(provider, movie, new CancellationToken(canceled: true)));
        Assert.Equal(4f, movie.CommunityRating);
    }

    [Fact]
    public async Task FetchAsync_NullItem_ReturnsNoUpdate()
    {
        using var scope = new PluginTestScope();
        var provider = new ImdbRatingsItemProvider(scope.Paths, NullLogger<ImdbRatingsItemProvider>.Instance);

        Assert.Equal(ItemUpdateType.None, await provider.FetchAsync((Movie)null!, null!, CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_DisabledProvider_DropsLoadedIndexBeforeReenable()
    {
        using var scope = new PluginTestScope();
        await WriteIndexAsync(scope);
        var provider = new ImdbRatingsItemProvider(scope.Paths, NullLogger<ImdbRatingsItemProvider>.Instance);
        Assert.Equal(ItemUpdateType.MetadataDownload, await FetchAsync(provider, CreateItem("Movie"), TestContext.Current.CancellationToken));
        scope.Configuration.EnableMetadataProvider = false;
        File.Delete(scope.IndexPath);
        var movie = CreateItem("Movie");

        Assert.Equal(ItemUpdateType.None, await FetchAsync(provider, movie, TestContext.Current.CancellationToken));
        scope.Configuration.EnableMetadataProvider = true;
        Assert.Equal(ItemUpdateType.None, await FetchAsync(provider, movie, TestContext.Current.CancellationToken));
        Assert.Equal(4f, movie.CommunityRating);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FetchAsync_DebugLogging_RequiresSettingAndLoggerLevel(bool settingEnabled, bool loggerEnabled)
    {
        using var scope = new PluginTestScope();
        await WriteIndexAsync(scope);
        scope.Configuration.EnableItemDebugLogging = settingEnabled;
        var logger = new RecordingLogger<ImdbRatingsItemProvider>(loggerEnabled);
        var provider = new ImdbRatingsItemProvider(scope.Paths, logger);

        Assert.Equal(ItemUpdateType.MetadataDownload, await FetchAsync(provider, CreateItem("Movie"), TestContext.Current.CancellationToken));

        var debugMessages = logger.Messages.Where(message => message.Level == LogLevel.Debug).ToArray();
        if (settingEnabled && loggerEnabled)
        {
            Assert.Contains("tt0000001", Assert.Single(debugMessages).Message);
        }
        else
        {
            Assert.Empty(debugMessages);
        }
    }

    private static BaseItem CreateItem(string kind)
    {
        BaseItem item = kind switch
        {
            "Movie" => new Movie(),
            "Series" => new Series(),
            "Episode" => new Episode(),
            _ => throw new ArgumentException("Unknown item type", nameof(kind))
        };
        item.CommunityRating = 4f;
        item.ProviderIds["Imdb"] = "tt0000001";
        return item;
    }

    private static Task<ItemUpdateType> FetchAsync(ImdbRatingsItemProvider provider, BaseItem item, CancellationToken token)
        => item switch
        {
            Movie movie => ((ICustomMetadataProvider<Movie>)provider).FetchAsync(movie, null!, token),
            Series series => ((ICustomMetadataProvider<Series>)provider).FetchAsync(series, null!, token),
            Episode episode => ((ICustomMetadataProvider<Episode>)provider).FetchAsync(episode, null!, token),
            _ => throw new ArgumentException("Unknown item type", nameof(item))
        };

    private static Task WriteIndexAsync(PluginTestScope scope)
        => ImdbRatingsIndex.CreateSorted(new uint[] { 1 }, new byte[] { 84 }, new uint[] { 500 })
            .WriteAsync(scope.IndexPath, CancellationToken.None);
}
