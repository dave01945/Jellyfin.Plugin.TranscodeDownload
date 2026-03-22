namespace Jellyfin.Plugin.TranscodeDownload.Models;

public enum DownloadJobState
{
    /// <summary>Accepted but not yet started (concurrency limit was not yet reached).</summary>
    Queued,

    /// <summary>ffmpeg is running.</summary>
    Running,

    /// <summary>ffmpeg exited with code 0; output file is ready for download.</summary>
    Completed,

    /// <summary>ffmpeg exited with a non-zero code or threw an exception.</summary>
    Failed,

    /// <summary>Cancelled by the user via DELETE /jobs/{id}.</summary>
    Cancelled,
}
