using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ImdbRatings.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    private int _minimumVotes = 1;

    public int MinimumVotes
    {
        get => _minimumVotes;
        set => _minimumVotes = Math.Clamp(value, 1, 1_000_000);
    }

    public bool IncludeMovies { get; set; } = true;

    public bool IncludeSeries { get; set; } = true;

    public bool IncludeSeasonAverages { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether ratings are also supplied at library-scan time.
    /// When enabled the scheduled task writes a compact index (~15 MB) that the metadata provider reads.
    /// </summary>
    public bool EnableMetadataProvider { get; set; } = true;

    public bool EnableItemDebugLogging { get; set; } = false;
}
