using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TranscodeDownload.Models;

/// <summary>
/// Encoder speed preset controlling the trade-off between encode speed and compression efficiency.
/// Maps to <c>-preset</c> (software/NVENC/QSV), <c>-quality</c> (AMF), or <c>-quality</c> integer (VAAPI).
/// VideoToolbox has no equivalent flag and is unaffected.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeedPreset
{
    /// <summary>Use the built-in per-backend default (current behavior).</summary>
    Default,
    /// <summary>Fastest encode, largest file. Software: ultrafast · NVENC: p1 · QSV: veryfast · AMF/VAAPI: fastest.</summary>
    Fastest,
    /// <summary>Software: veryfast · NVENC: p2 · QSV: veryfast · AMF: speed · VAAPI: 6.</summary>
    VeryFast,
    /// <summary>Software: fast · NVENC: p4 · QSV: fast · AMF: speed · VAAPI: 5.</summary>
    Fast,
    /// <summary>Balanced encode. Software: medium · NVENC: p5 · QSV: medium · AMF: balanced · VAAPI: 3.</summary>
    Medium,
    /// <summary>Better compression, slower. Software: slow · NVENC: p6 · QSV: slow · AMF/VAAPI: quality.</summary>
    Slow,
    /// <summary>Best compression. Software: veryslow · NVENC: p7 · QSV: veryslow · AMF/VAAPI: best quality.</summary>
    VerySlow,
}

/// <summary>Named quality presets. When set to anything other than <see cref="Custom"/>, overrides <see cref="CreateJobRequest.Crf"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityPreset
{
    /// <summary>Use explicit <see cref="CreateJobRequest.Crf"/> or <see cref="CreateJobRequest.VideoBitrate"/> values.</summary>
    Custom,
    /// <summary>Low quality — smallest file. Maps to CRF 28.</summary>
    Low,
    /// <summary>Balanced quality. Maps to CRF 23.</summary>
    Medium,
    /// <summary>High quality. Maps to CRF 18.</summary>
    High,
    /// <summary>Very high quality — near lossless for most content. Maps to CRF 15.</summary>
    VeryHigh,
}

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

    /// <summary>
    /// Named quality preset. When set to anything other than <see cref="QualityPreset.Custom"/>,
    /// overrides <see cref="Crf"/> with a sensible default for the chosen tier.
    /// Has no effect when <see cref="VideoBitrate"/> is set.
    /// </summary>
    public QualityPreset Preset { get; set; } = QualityPreset.Custom;

    /// <summary>
    /// Encoder speed preset. Controls the encode speed / compression trade-off via ffmpeg's
    /// <c>-preset</c> (software/NVENC/QSV), <c>-quality</c> (AMF), or <c>-quality</c> integer (VAAPI).
    /// <see cref="SpeedPreset.Default"/> preserves the per-backend built-in default.
    /// </summary>
    public SpeedPreset SpeedPreset { get; set; } = SpeedPreset.Default;

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
    /// When true, audio is encoded in VBR mode using the codec's native quality parameter.
    /// <list type="bullet">
    ///   <item><description>aac — <c>-vbr 1–5</c> (derived from <see cref="AudioBitrate"/>; replaces <c>-b:a</c>).</description></item>
    ///   <item><description>libmp3lame — <c>-q:a 0–9</c> (0 = best; derived from <see cref="AudioBitrate"/>; replaces <c>-b:a</c>).</description></item>
    ///   <item><description>libopus — <c>-vbr on</c> with <c>-b:a</c> used as a target bitrate.</description></item>
    ///   <item><description>flac — lossless; flag has no effect.</description></item>
    /// </list>
    /// Default false (CBR) for backward compatibility.
    /// </summary>
    public bool AudioVbr { get; set; } = false;

    /// <summary>
    /// When true and <see cref="VideoBitrate"/> is set, uses constrained-VBR mode:
    /// <c>-b:v</c> (target), <c>-maxrate</c> (1.5× target), <c>-bufsize</c> (2× target).
    /// Has no effect when <see cref="VideoBitrate"/> is null (CRF mode is already VBR).
    /// Default false for backward compatibility.
    /// </summary>
    public bool VideoVbr { get; set; } = false;

    /// <summary>
    /// Output audio channel count. Null = keep the source channel layout.
    /// Common values: 1 (mono), 2 (stereo), 6 (5.1 surround), 8 (7.1 surround).
    /// </summary>
    [Range(1, 8)]
    public int? AudioChannels { get; set; }

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
