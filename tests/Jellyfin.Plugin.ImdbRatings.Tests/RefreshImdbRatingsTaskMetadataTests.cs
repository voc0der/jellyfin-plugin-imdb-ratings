using Jellyfin.Plugin.ImdbRatings.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// Covers the task's registration surface: the schedule Jellyfin reads when the plugin is first installed.
/// </summary>
public class RefreshImdbRatingsTaskMetadataTests
{
    [Fact]
    public void GetDefaultTriggers_IsASingleDailyTriggerAtThreeAm()
    {
        var trigger = Assert.Single(CreateTask().GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void TaskIdentity_IsStable()
    {
        var task = CreateTask();

        // The key is what Jellyfin persists against a user's saved schedule; changing it silently resets it.
        Assert.Equal("RefreshImdbRatings", task.Key);
        Assert.Equal("Refresh IMDb Ratings", task.Name);
        Assert.Equal("IMDb Ratings", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }

    private static RefreshImdbRatingsTask CreateTask()
    {
        using var temp = new TempDirectory();

        // Only DataPath is read by the constructor; nothing here reaches the library or the network.
        return new RefreshImdbRatingsTask(
            libraryManager: null!,
            httpClientFactory: null!,
            NullLogger<RefreshImdbRatingsTask>.Instance,
            NullLoggerFactory.Instance,
            new StubApplicationPaths(temp.Path));
    }

    private sealed class StubApplicationPaths : IApplicationPaths
    {
        public StubApplicationPaths(string dataPath)
        {
            DataPath = dataPath;
        }

        public string DataPath { get; }

        public string ProgramDataPath => DataPath;

        public string WebPath => DataPath;

        public string ProgramSystemPath => DataPath;

        public string ImageCachePath => DataPath;

        public string PluginsPath => DataPath;

        public string PluginConfigurationsPath => DataPath;

        public string LogDirectoryPath => DataPath;

        public string ConfigurationDirectoryPath => DataPath;

        public string SystemConfigurationFilePath => DataPath;

        public string CachePath => DataPath;

        public string TempDirectory => DataPath;

        public string VirtualDataPath => DataPath;

        public string TrickplayPath => DataPath;

        public string BackupPath => DataPath;

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive)
        {
        }
    }
}
