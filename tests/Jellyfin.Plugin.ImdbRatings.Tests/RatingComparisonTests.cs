using Jellyfin.Plugin.ImdbRatings.Providers;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

/// <summary>
/// The scheduled task and the scan-time provider both decide "has this rating changed?", and they must give
/// the same answer. If they diverge, an item's rating is rewritten by whichever path runs last, forever.
/// </summary>
public class RatingComparisonTests
{
    [Fact]
    public void IsUnchanged_NoExistingRating_IsAlwaysAChange()
    {
        Assert.False(RatingComparison.IsUnchanged(null, 7.4f));
    }

    [Fact]
    public void IsUnchanged_IdenticalRating_IsUnchanged()
    {
        Assert.True(RatingComparison.IsUnchanged(7.4f, 7.4f));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.001f)]
    [InlineData(0.009f)]
    public void IsUnchanged_DifferenceBelowTolerance_IsUnchanged(float delta)
    {
        Assert.True(RatingComparison.IsUnchanged(7.4f, 7.4f + delta));
        Assert.True(RatingComparison.IsUnchanged(7.4f, 7.4f - delta));
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    [InlineData(3.0f)]
    public void IsUnchanged_DifferenceAboveTolerance_IsAChange(float delta)
    {
        Assert.False(RatingComparison.IsUnchanged(7.4f, 7.4f + delta));
        Assert.False(RatingComparison.IsUnchanged(7.4f, 7.4f - delta));
    }

    [Fact]
    public void IsUnchanged_DifferenceExactlyAtTolerance_IsAChange()
    {
        // Strictly-less-than, so the boundary itself counts as a change.
        Assert.False(RatingComparison.IsUnchanged(7.4f, 7.4f + RatingComparison.Tolerance));
    }

    [Fact]
    public void Tolerance_IsTighterThanTheSmallestRatingImdbPublishes()
    {
        // IMDb publishes one decimal place, so every real change is at least 0.1.
        Assert.True(RatingComparison.Tolerance < 0.1f);
    }

    [Fact]
    public void IsUnchanged_SurvivesTheIndexRoundTrip()
    {
        // A rating that went through the byte-encoded index must compare equal to its source value, or the
        // provider would rewrite every item on every scan.
        for (int scaled = 10; scaled <= 100; scaled++)
        {
            var original = scaled / 10f;
            var roundTripped = ImdbRatingsIndex.EncodeRating(original) / 10f;

            Assert.True(
                RatingComparison.IsUnchanged(original, roundTripped),
                $"Rating {original} did not survive the index round trip");
        }
    }
}
