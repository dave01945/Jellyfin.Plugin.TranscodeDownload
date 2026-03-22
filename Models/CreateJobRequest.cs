using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.TranscodeDownload.Models;

/// <summary>POST /Plugins/TranscodeDownload/jobs request body.</summary>
public sealed class CreateJobRequest
{
    /// <summary>Jellyfin library item ID (BaseItem.Id).</summary>
    [Required]
    public Guid ItemId { get; set; }

    /// <summary>
    /// Optional: specific media source ID to transcode (e.g. a particular version of the file).
    /// Defaults to the first available source.
    /// </summary>
    public string? MediaSourceId { get; set; }

    // ── Video ────────────────────────────────────────────────────────────────

    /// <summary>Output video codec: "h264" (default), "hevc", "vp9".</summary>
    public string VideoCodec { get; set; } = "h264";

    /// <summary>
    /// Video bitrate in bits per second.
    /// When null a CRF-based quality target is used instead (recommended for file download).
    /// </summary>
    public int? VideoBitrate { get; set; }

    /// <summary>CRF quality value used when <see cref="VideoBitrate"/> is null. Lower = better quality.</summary>
    [Range(0, 51)]
    public int Crf { get; set; } = 23;

    /// <summary>Maximum output width in pixels. Aspect ratio is preserved. Null = source width.</summary>
    public int? MaxWidth { get; set; }

    /// <summary>Maximum output height in pixels. Aspect ratio is preserved. Null = source height.</summary>
    public int? MaxHeight { get; set; }

    // ── Audio ─────────────────────────────────────────────────────────────────

    /// <summary>Output audio codec: "aac" (default), "mp3", "opus", "flac".</summary>
    public string AudioCodec { get; set; } = "aac";

    /// <summary>Audio bitrate in bits per second. Default 128 kbps.</summary>
    [Range(8_000, 640_000)]
    public int AudioBitrate { get; set; } = 128_000;

    /// <summary>
    /// Zero-based audio stream index within the source file.
    /// -1 (default) picks the first audio stream automatically.
    /// </summary>
    [Range(-1, int.MaxValue)]
    public int AudioStreamIndex { get; set; } = -1;

    // ── Subtitles ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Zero-based subtitle stream index to mux as a soft subtitle track in the output container (mov_text).
    /// -1 (default) = no subtitles.
    /// Note: image-based (PGS/VOBSUB) streams require burn-in via a separate video filter — not
    /// yet supported; use a text-based stream (SRT/ASS converted to mov_text).
    /// </summary>
    [Range(-1, int.MaxValue)]
    public int SubtitleStreamIndex { get; set; } = -1;

    // ── Container ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Output container format. Accepted values: "mp4" (default), "mkv", "webm", "avi".
    /// The chosen container must be compatible with the selected video/audio codecs.
    /// </summary>
    public string ContainerFormat { get; set; } = "mp4";
}
