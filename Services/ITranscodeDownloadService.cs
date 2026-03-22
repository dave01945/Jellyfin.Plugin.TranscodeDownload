using Jellyfin.Plugin.TranscodeDownload.Models;

namespace Jellyfin.Plugin.TranscodeDownload.Services;

public interface ITranscodeDownloadService
{
    /// <summary>
    /// Resolves the library item, builds ffmpeg arguments, and launches the transcode process.
    /// Safe to call on the request thread — no HttpContext dependency.
    /// </summary>
    /// <returns>The new job ID.</returns>
    /// <exception cref="ArgumentException">Item not found or no media source available.</exception>
    /// <exception cref="InvalidOperationException">Concurrency limit reached.</exception>
    Task<Guid> StartJobAsync(CreateJobRequest request, CancellationToken cancellationToken);

    /// <summary>Returns the status DTO for a job, or null if the ID is unknown.</summary>
    JobStatusDto? GetJob(Guid jobId);

    /// <summary>Returns all currently tracked jobs.</summary>
    IEnumerable<JobStatusDto> GetAllJobs();

    /// <summary>
    /// Resolves the output file path and current state for a job.
    /// </summary>
    /// <returns>False if the job ID is not registered.</returns>
    bool TryGetOutputInfo(Guid jobId, out string? outputPath, out DownloadJobState state);

    /// <summary>Signals the running ffmpeg process to stop.</summary>
    bool CancelJob(Guid jobId);

    /// <summary>
    /// Cancels the job (if running), deletes the output file, and removes the registry entry.
    /// </summary>
    void DeleteJob(Guid jobId);
}
