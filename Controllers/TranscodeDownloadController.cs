using System.IO;
using Jellyfin.Plugin.TranscodeDownload.Models;
using Jellyfin.Plugin.TranscodeDownload.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TranscodeDownload.Controllers;

/// <summary>
/// REST API for managing transcode-download jobs.
///
/// All routes are under <c>/Plugins/TranscodeDownload/</c>.
/// The controller is auto-discovered by Jellyfin by scanning the plugin assembly for
/// classes inheriting <see cref="ControllerBase"/>.
/// </summary>
[ApiController]
[Route("Plugins/TranscodeDownload")]
[Authorize]  // Requires a valid Jellyfin API key or session token.
public sealed class TranscodeDownloadController : ControllerBase
{
    private readonly ITranscodeDownloadService _service;

    public TranscodeDownloadController(ITranscodeDownloadService service)
    {
        _service = service;
    }

    // ── POST /Plugins/TranscodeDownload/jobs ──────────────────────────────────

    /// <summary>
    /// Starts a new transcode-download job for a library item.
    /// </summary>
    /// <param name="request">Encoding parameters and the target item ID.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>202 Accepted with <c>{ jobId: "..." }</c> on success.</returns>
    [HttpPost("jobs")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreateJob(
        [FromBody] CreateJobRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (request.ItemId == Guid.Empty)
            return BadRequest("ItemId must be a non-empty GUID.");

        try
        {
            var jobId = await _service.StartJobAsync(request, cancellationToken);
            return Accepted(new { jobId });
        }
        catch (InvalidOperationException ex)
        {
            // Concurrency limit
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            // Item or media source not found
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            // Temporary catch-all to surface unexpected exceptions during debugging.
            // Remove once root cause is identified.
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = ex.GetType().FullName,
                message = ex.Message,
                stackTrace = ex.StackTrace,
            });
        }
    }

    // ── GET /Plugins/TranscodeDownload/jobs ───────────────────────────────────

    /// <summary>Returns all currently tracked jobs.</summary>
    [HttpGet("jobs")]
    [ProducesResponseType(typeof(IEnumerable<JobStatusDto>), StatusCodes.Status200OK)]
    public IActionResult GetAllJobs() => Ok(_service.GetAllJobs());

    // ── GET /Plugins/TranscodeDownload/jobs/{id} ──────────────────────────────

    /// <summary>Returns the status and progress for a specific job.</summary>
    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType(typeof(JobStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetJob(Guid id)
    {
        var dto = _service.GetJob(id);
        return dto is not null ? Ok(dto) : NotFound();
    }

    // ── GET /Plugins/TranscodeDownload/jobs/{id}/file ─────────────────────────

    /// <summary>
    /// Downloads the transcoded MP4 file once the job is complete.
    ///
    /// <list type="bullet">
    ///   <item><term>Queued / Running</term><description>202 Accepted — poll <c>GET /jobs/{id}</c> until state is Completed.</description></item>
    ///   <item><term>Completed</term><description>200 with the MP4 file (supports range requests).</description></item>
    ///   <item><term>Failed / Cancelled</term><description>409 Conflict.</description></item>
    /// </list>
    /// </summary>
    [HttpGet("jobs/{id:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult DownloadFile(Guid id)
    {
        if (!_service.TryGetOutputInfo(id, out var path, out var state))
            return NotFound();

        return state switch
        {
            DownloadJobState.Queued or DownloadJobState.Running =>
                StatusCode(StatusCodes.Status202Accepted, new
                {
                    message = "Transcode is still in progress. Poll GET /jobs/{id} for status.",
                    state = state.ToString(),
                }),

            DownloadJobState.Completed =>
                // enableRangeProcessing: true — lets the client resume interrupted downloads
                // and skip to any position without re-downloading from the beginning.
                PhysicalFile(path!, GetContentType(path!),
                    fileDownloadName: Path.GetFileName(path!),
                    enableRangeProcessing: true),

            _ => // Failed, Cancelled
                StatusCode(StatusCodes.Status409Conflict, new
                {
                    message = $"Job is in state '{state}' and no file is available.",
                    state = state.ToString(),
                }),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mkv"  => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi"  => "video/x-msvideo",
            _       => "video/mp4",
        };

    // ── DELETE /Plugins/TranscodeDownload/jobs/{id} ───────────────────────────

    /// <summary>
    /// Cancels a running job (if applicable), deletes the output file, and removes the registry entry.
    /// </summary>
    [HttpDelete("jobs/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteJob(Guid id)
    {
        if (_service.GetJob(id) is null)
            return NotFound();

        _service.DeleteJob(id);
        return NoContent();
    }
}
