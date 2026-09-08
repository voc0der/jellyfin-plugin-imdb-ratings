namespace Jellyfin.Plugin.ImdbRatings.Providers;

/// <summary>
/// Shared policy for deciding whether an IMDb rating differs enough from an item's current
/// <c>CommunityRating</c> to be worth persisting.
/// </summary>
/// <remarks>
/// The scheduled task and the scan-time metadata provider both make this decision, and they must agree:
/// if one treats a value as changed while the other treats it as unchanged, an item's rating flips back and
/// forth between the two paths on every refresh. This lives in one place so the tolerance cannot drift.
/// </remarks>
public static class RatingComparison
{
    /// <summary>
    /// Ratings closer together than this are treated as equal. IMDb publishes one decimal place, so anything
    /// below 0.1 is comfortably tighter than the smallest real change while absorbing float round-tripping.
    /// </summary>
    public const float Tolerance = 0.01f;

    /// <summary>
    /// Returns true when <paramref name="current"/> already holds <paramref name="candidate"/>.
    /// An item with no rating is always considered changed.
    /// </summary>
    public static bool IsUnchanged(float? current, float candidate)
    {
        return current.HasValue && System.Math.Abs(current.Value - candidate) < Tolerance;
    }
}
