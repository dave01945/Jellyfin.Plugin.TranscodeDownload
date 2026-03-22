using Jellyfin.Plugin.TranscodeDownload.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TranscodeDownload;

/// <summary>
/// Registers plugin-owned services into Jellyfin's DI container.
/// Jellyfin discovers this class by scanning the plugin assembly for <c>IPluginServiceRegistrator</c>.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singleton: one job registry for the lifetime of the server.
        serviceCollection.AddSingleton<ITranscodeDownloadService, TranscodeDownloadService>();
    }
}
