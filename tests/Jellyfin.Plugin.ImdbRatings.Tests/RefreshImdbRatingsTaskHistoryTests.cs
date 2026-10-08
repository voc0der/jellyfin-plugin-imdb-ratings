using System.Net;
using System.Reflection;
using Jellyfin.Plugin.ImdbRatings.Api;
using Jellyfin.Plugin.ImdbRatings.ScheduledTasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class RefreshImdbRatingsTaskHistoryTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ExecuteAsync_HttpFailure_RecordsStatusCodeAndStillThrows(HttpStatusCode statusCode)
    {
        using var temp = new TempDirectory();
        using var handler = new ResponseHandler(statusCode);
        var task = CreateTask(temp, handler, new[] { CreateMovie() });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        var run = ReadRun(temp);
        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("Failed", run.Status);
        Assert.Contains($"HTTP {(int)statusCode}", run.Summary);
        Assert.True(run.FinishedAtUtc >= run.StartedAtUtc);
        Assert.DoesNotContain("https://", run.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CachedData_RecordsSuccessOrStaleFallback(bool stale)
    {
        using var temp = new TempDirectory();
        await WriteCacheAsync(temp, stale);
        using var handler = new ResponseHandler(HttpStatusCode.Forbidden);

        await CreateTask(temp, handler, new[] { CreateMovie() })
            .ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var run = ReadRun(temp);
        Assert.Equal(stale ? "Warning" : "Completed", run.Status);
        Assert.Equal("0 ratings updated; 1 skipped; 0 not found.", run.Summary);
        if (stale)
        {
            Assert.Contains(run.Warnings, warning => warning.Contains("older cached") && warning.Contains("HTTP 403"));
        }
        else
        {
            Assert.Empty(run.Warnings);
            Assert.Equal(0, handler.Calls);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyLibraryIndexDownloadFails_RecordsWarningWithoutThrowing()
    {
        using var temp = new TempDirectory();
        using var handler = new ResponseHandler(HttpStatusCode.NotFound);

        await CreateTask(temp, handler, Array.Empty<BaseItem>())
            .ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var run = ReadRun(temp);
        Assert.Equal("Warning", run.Status);
        Assert.Contains("No eligible library items", run.Summary);
        Assert.Contains(run.Warnings, warning => warning.Contains("index was not refreshed") && warning.Contains("HTTP 404"));
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_RecordsCancellationAndStillThrows()
    {
        using var temp = new TempDirectory();
        using var handler = new ResponseHandler(HttpStatusCode.OK);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateTask(temp, handler, new[] { CreateMovie() })
                .ExecuteAsync(new Progress<double>(), cancellation.Token));

        Assert.Equal("Cancelled", ReadRun(temp).Status);
    }

    [Fact]
    public async Task ExecuteAsync_HistoryWriteFails_DoesNotFailSuccessfulRefresh()
    {
        using var temp = new TempDirectory();
        await WriteCacheAsync(temp, stale: false);
        File.WriteAllText(temp.PathFor("imdb-ratings"), "A file blocks the history directory.");
        using var handler = new ResponseHandler(HttpStatusCode.Forbidden);

        await CreateTask(temp, handler, new[] { CreateMovie() })
            .ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_HistoryWriteFails_DoesNotHideOriginalFailure()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.PathFor("imdb-ratings"), "A file blocks the history directory.");
        var original = new InvalidOperationException("Library unavailable.");
        var library = TestServiceProxy.Create<ILibraryManager>((_, _) => throw original);
        var task = new RefreshImdbRatingsTask(library, null!, NullLogger<RefreshImdbRatingsTask>.Instance,
            NullLoggerFactory.Instance, CreatePaths(temp));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        Assert.Same(original, actual);
    }

    [Fact]
    public void HistoryEndpoint_ReturnsPersistedRunsAndRequiresAdministrator()
    {
        using var temp = new TempDirectory();
        new RefreshRunHistory(temp.Path, NullLogger.Instance).Append(new RefreshRun { Summary = "Saved run" });
        var controller = new RunHistoryController(CreatePaths(temp), NullLogger<RunHistoryController>.Instance);

        var response = Assert.IsType<OkObjectResult>(controller.GetRunHistory().Result);
        var runs = Assert.IsAssignableFrom<IReadOnlyList<RefreshRun>>(response.Value);
        Assert.Equal("Saved run", Assert.Single(runs).Summary);
        Assert.Equal(Policies.RequiresElevation, typeof(RunHistoryController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    private static RefreshRun ReadRun(TempDirectory temp)
        => Assert.Single(new RefreshRunHistory(temp.Path, NullLogger.Instance).Read());

    private static Movie CreateMovie()
    {
        var movie = new Movie { Name = "Test", Id = Guid.NewGuid(), CommunityRating = 7.5f };
        movie.ProviderIds["Imdb"] = "tt0000001";
        return movie;
    }

    private static RefreshImdbRatingsTask CreateTask(TempDirectory temp, ResponseHandler handler, IReadOnlyList<BaseItem> items)
    {
        var library = TestServiceProxy.Create<ILibraryManager>((method, _) => method.Name == nameof(ILibraryManager.GetItemList)
            ? items
            : throw new InvalidOperationException($"Unexpected library call: {method.Name}"));
        return new RefreshImdbRatingsTask(library, new ClientFactory(handler), NullLogger<RefreshImdbRatingsTask>.Instance,
            NullLoggerFactory.Instance, CreatePaths(temp));
    }

    private static IApplicationPaths CreatePaths(TempDirectory temp)
        => TestServiceProxy.Create<IApplicationPaths>((method, _) => method.Name == "get_DataPath"
            ? temp.Path
            : throw new InvalidOperationException($"Unexpected paths call: {method.Name}"));

    private static async Task WriteCacheAsync(TempDirectory temp, bool stale)
    {
        var directory = Directory.CreateDirectory(temp.PathFor("imdb-ratings-cache"));
        var path = Path.Join(directory.FullName, "title.ratings.tsv");
        using (var writer = new StreamWriter(path))
        {
            await writer.WriteLineAsync("tconst\taverageRating\tnumVotes");
            for (var i = 1; i <= 500_000; i++)
            {
                await writer.WriteLineAsync($"tt{i:0000000}\t7.5\t100");
            }
        }

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(stale ? -24 : -1));
    }

    private sealed class ResponseHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
