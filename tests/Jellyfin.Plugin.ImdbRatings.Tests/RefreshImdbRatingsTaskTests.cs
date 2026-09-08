using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.ScheduledTasks;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class RefreshImdbRatingsTaskTests
{
    [Fact]
    public void IsTransientNetworkError_HttpTimeoutWithoutSchedulerCancellation_ReturnsTrue()
    {
        Assert.True(RefreshImdbRatingsTask.IsTransientNetworkError(
            new TaskCanceledException("Simulated HTTP timeout."),
            CancellationToken.None));
    }

    [Fact]
    public void IsTransientNetworkError_SchedulerCancellation_ReturnsFalse()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(RefreshImdbRatingsTask.IsTransientNetworkError(
            new OperationCanceledException(cancellation.Token),
            cancellation.Token));
    }

    [Fact]
    public void CreateExhaustedTimeoutException_ProducesNonCancellationFailure()
    {
        var timeout = new TaskCanceledException("Simulated HTTP timeout.");

        var translated = RefreshImdbRatingsTask.CreateExhaustedTimeoutException(timeout);

        Assert.Same(timeout, translated.InnerException);
        Assert.IsNotAssignableFrom<OperationCanceledException>(translated);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void ResolveMetadataProviderEnabled_CurrentConfigurationWinsOverTaskSnapshot(
        bool taskValue,
        bool currentValue,
        bool expected)
    {
        var taskConfiguration = new PluginConfiguration { EnableMetadataProvider = taskValue };
        var currentConfiguration = new PluginConfiguration { EnableMetadataProvider = currentValue };

        Assert.Equal(
            expected,
            RefreshImdbRatingsTask.ResolveMetadataProviderEnabled(taskConfiguration, currentConfiguration));
    }
}
