using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TranscodeDownload.Configuration;

/// <summary>Hardware acceleration backend for ffmpeg video encoding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HardwareAccelMode
{
    /// <summary>Software encoding (CPU only). Most compatible.</summary>
    None,
    /// <summary>NVIDIA NVENC (CUDA). Requires NVIDIA GPU and drivers.</summary>
    Nvenc,
    /// <summary>Intel Quick Sync Video. Requires Intel GPU/iGPU and drivers.</summary>
    Qsv,
    /// <summary>VA-API (Linux). Requires a VA-API-capable GPU and libva.</summary>
    Vaapi,
    /// <summary>Apple VideoToolbox (macOS only).</summary>
    VideoToolbox,
    /// <summary>AMD AMF. Requires AMD GPU and AMF runtime.</summary>
    Amf,
}

/// <summary>
/// Persisted plugin settings — editable via Jellyfin Dashboard → Plugins.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Maximum number of ffmpeg transcode processes that may run simultaneously.
    /// Requests that exceed this limit receive HTTP 429.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>
    /// Optional override for the directory where transcoded MP4 files are written.
    /// When null, defaults to <c>{Jellyfin TranscodePath}/downloads/</c>.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Minutes before a completed or failed job is evicted from the in-memory registry.
    /// The output file is also deleted on eviction unless <see cref="KeepFilesAfterEviction"/> is true.
    /// </summary>
    public int JobRetentionMinutes { get; set; } = 60;

    /// <summary>
    /// When true the transcoded MP4 file is kept on disk after the job is evicted from the registry.
    /// When false (default) the file is deleted together with the registry entry.
    /// </summary>
    public bool KeepFilesAfterEviction { get; set; } = false;

    /// <summary>
    /// Hardware acceleration backend to use for video encoding.
    /// Leave as <see cref="HardwareAccelMode.None"/> to use CPU-based (software) encoding.
    /// </summary>
    public HardwareAccelMode HardwareAcceleration { get; set; } = HardwareAccelMode.None;

    /// <summary>
    /// VA-API render device path (Linux only).
    /// Only used when <see cref="HardwareAcceleration"/> is <see cref="HardwareAccelMode.Vaapi"/>.
    /// </summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
}
