namespace Jellyfin.Plugin.TranscodeDownload.Models;

/// <summary>Response DTO for GET /Plugins/TranscodeDownload/jobs/{id}.</summary>
public sealed class JobStatusDto
{
    public Guid JobId { get; init; }
    public Guid ItemId { get; init; }

    /// <summary>Display name of the library item (e.g. episode or movie title).</summary>
    public string ItemName { get; init; } = string.Empty;

    public DownloadJobState State { get; init; }

    /// <summary>Rough progress percentage 0–100. Null while state is Queued.</summary>
    public double? ProgressPercent { get; init; }

    /// <summary>Byte size of the output file. Populated only when State == Completed.</summary>
    public long? OutputSizeBytes { get; init; }

    /// <summary>
    /// Relative API path to download the transcoded file when State == Completed.
    /// Null for non-completed jobs.
    /// </summary>
    public string? DownloadUrl { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Human-readable error description when State == Failed.</summary>
    public string? Error { get; init; }
}
