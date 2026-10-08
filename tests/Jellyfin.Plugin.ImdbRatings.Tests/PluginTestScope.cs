using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

// Plugin.Instance and the shared index belong to the process. Tests that change them must also
// exclude other collections, whose task/provider tests rely on the default configuration.
[CollectionDefinition("Plugin state", DisableParallelization = true)]
public sealed class PluginStateCollection;

internal sealed class PluginTestScope : IDisposable
{
    private readonly Plugin? _previousInstance = Plugin.Instance;
    private readonly ILibraryManager? _previousLibraryManager = BaseItem.LibraryManager;

    public PluginTestScope()
    {
        Paths = TestServiceProxy.Create<IApplicationPaths>((method, _) => method.Name switch
        {
            "get_DataPath" or "get_PluginConfigurationsPath" or "get_PluginsPath" => Temp.Path,
            _ => throw new InvalidOperationException($"Unexpected paths call: {method.Name}")
        });
        var serializer = TestServiceProxy.Create<IXmlSerializer>((method, args) =>
        {
            if (method.Name == nameof(IXmlSerializer.SerializeToFile))
            {
                using var stream = File.Create((string)args![1]!);
                new XmlSerializer(args[0]!.GetType()).Serialize(stream, args[0]);
                return null;
            }

            if (method.Name == nameof(IXmlSerializer.DeserializeFromFile))
            {
                using var stream = File.OpenRead((string)args![1]!);
                return new XmlSerializer((Type)args[0]!).Deserialize(stream);
            }

            throw new InvalidOperationException($"Unexpected serializer call: {method.Name}");
        });
        Plugin = new Plugin(Paths, serializer);
    }

    public TempDirectory Temp { get; } = new();

    public IApplicationPaths Paths { get; }

    public Plugin Plugin { get; }

    public PluginConfiguration Configuration => Plugin.Configuration;

    public string IndexPath => ImdbRatingsIndex.GetIndexPath(Temp.Path);

    public void Dispose()
    {
        ImdbRatingsIndexCache.InvalidateShared();
        BaseItem.LibraryManager = _previousLibraryManager;
        typeof(Plugin).GetProperty(nameof(Plugin.Instance), BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, _previousInstance);
        Temp.Dispose();
    }
}
