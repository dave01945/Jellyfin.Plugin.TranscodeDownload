using System.Collections.Generic;
using Jellyfin.Plugin.TranscodeDownload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TranscodeDownload;

/// <summary>
/// Plugin entry point. Jellyfin discovers this via <c>BasePlugin&lt;T&gt;</c> reflection.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Singleton access used by the service to read live configuration.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Transcode Download";

    /// <summary>
    /// Stable plugin GUID — never change this after initial deployment or Jellyfin will treat
    /// the plugin as a different one and lose user settings.
    /// </summary>
    public override Guid Id => Guid.Parse("3c4b5d6e-7f8a-9b0c-1d2e-3f4a5b6c7d8e");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        };
    }
}
