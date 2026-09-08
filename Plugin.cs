using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ImdbRatings;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "IMDb Ratings";

    public override Guid Id => Guid.Parse("f5a3c7e1-9b2d-4f6a-8e0c-1d3b5a7c9e2f");

    public override string Description => "Downloads the IMDb ratings flat file daily and updates CommunityRating on all library items with an IMDb ID.";

    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);

        if (configuration is not PluginConfiguration { EnableMetadataProvider: false })
        {
            return;
        }

        // Apply the memory/disk saving promised by the toggle immediately. The scheduled task repeats this
        // cleanup with logging, so a transient sharing or permission failure is retried on its next run.
        ImdbRatingsIndexCache.InvalidateShared();
        try
        {
            File.Delete(ImdbRatingsIndex.GetIndexPath(ApplicationPaths.DataPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Configuration has already been saved. Do not turn a best-effort cache cleanup into a failed
            // settings update; the scheduled task will retry and log the failure.
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
