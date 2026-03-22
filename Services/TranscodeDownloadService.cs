using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Jellyfin.Plugin.TranscodeDownload.Configuration;
using Jellyfin.Plugin.TranscodeDownload.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeDownload.Services;

public sealed class TranscodeDownloadService : ITranscodeDownloadService, IDisposable
{
    // ── Internal job record ──────────────────────────────────────────────────

    private sealed class DownloadJob
    {
        public Guid JobId { get; init; }
        public Guid ItemId { get; init; }
        public string OutputPath { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }

        public DownloadJobState State { get; set; } = DownloadJobState.Queued;
        public double? ProgressPercent { get; set; }
        public string? Error { get; set; }

        /// <summary>Running ffmpeg process. Null when not running.</summary>
        public Process? FfmpegProcess { get; set; }

        /// <summary>
        /// PID of the ffmpeg process, set immediately after Start(). -1 before start.
        /// Volatile ensures the delete thread always reads the freshest value.
        /// </summary>
        public volatile int FfmpegPid = -1;

        /// <summary>Linked to the original request token so external cancellation also kills ffmpeg.</summary>
        public CancellationTokenSource? Cts { get; set; }
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<TranscodeDownloadService> _logger;

    private int _runningCount;
    private bool _disposed;

    private PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ── Constructor ──────────────────────────────────────────────────────────

    public TranscodeDownloadService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager configManager,
        ILogger<TranscodeDownloadService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _configManager = configManager;
        _logger = logger;
    }

    // ── ITranscodeDownloadService ────────────────────────────────────────────

    public async Task<Guid> StartJobAsync(CreateJobRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Atomically increment and check the limit to prevent TOCTOU races when multiple
        // requests arrive simultaneously.
        var slotCount = Interlocked.Increment(ref _runningCount);
        if (slotCount > Config.MaxConcurrentJobs)
        {
            Interlocked.Decrement(ref _runningCount);
            throw new InvalidOperationException(
                $"Too many concurrent transcode jobs (limit: {Config.MaxConcurrentJobs}).");
        }

        // _runningCount is now incremented. Any early-exit path below that doesn't launch
        // RunFfmpegAsync must decrement it via the `launched` guard in the finally block.
        bool launched = false;
        try
        {
            // 1. Resolve item from library.
            var item = _libraryManager.GetItemById(request.ItemId)
                ?? throw new ArgumentException($"Item {request.ItemId} not found in the library.");

            // 2. Determine the input file path.
            //    For multi-version items, respect the requested MediaSourceId.
            string inputPath;
            if (!string.IsNullOrEmpty(request.MediaSourceId))
            {
                inputPath = await GetPathForMediaSourceAsync(
                    item, request.ItemId, request.MediaSourceId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (string.IsNullOrEmpty(item.Path))
                    throw new InvalidOperationException(
                        $"Item {request.ItemId} has no local file path. Only locally-stored media is supported.");
                inputPath = item.Path;
            }

            // 3. Prepare output path.
            var outputDir = string.IsNullOrEmpty(Config.OutputDirectory)
                ? Path.Combine(_configManager.GetTranscodePath(), "downloads")
                : Config.OutputDirectory;
            Directory.CreateDirectory(outputDir);

            var jobId = Guid.NewGuid();
            var outputPath = Path.Combine(outputDir, $"{jobId}.mp4");

            // 4. Register job before launching so status is visible immediately.
            var job = new DownloadJob
            {
                JobId = jobId,
                ItemId = request.ItemId,
                OutputPath = outputPath,
                CreatedAt = DateTimeOffset.UtcNow,
                State = DownloadJobState.Running,
                Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
            };
            _jobs[jobId] = job;

            _logger.LogInformation(
                "Transcode download job {JobId} created for item {ItemId} → {OutputPath} (HWAccel={HwAccel})",
                jobId, request.ItemId, outputPath, Config.HardwareAcceleration);

            // 5. Launch ffmpeg in the background — it now owns the Decrement in its finally block.
            _ = RunFfmpegAsync(job, inputPath, request);
            launched = true;

            return jobId;
        }
        finally
        {
            // Only decrement if RunFfmpegAsync was NOT launched; if it was, it owns the Decrement.
            if (!launched)
                Interlocked.Decrement(ref _runningCount);
        }
    }

    public JobStatusDto? GetJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return null;

        long? sizeBytes = null;
        if (job.State == DownloadJobState.Completed && File.Exists(job.OutputPath))
            sizeBytes = new FileInfo(job.OutputPath).Length;

        return new JobStatusDto
        {
            JobId = job.JobId,
            ItemId = job.ItemId,
            State = job.State,
            ProgressPercent = job.ProgressPercent,
            OutputSizeBytes = sizeBytes,
            // Always expose the deterministic file endpoint; it returns 202 until complete.
            DownloadUrl = $"/Plugins/TranscodeDownload/jobs/{job.JobId}/file",
            CreatedAt = job.CreatedAt,
            Error = job.Error,
        };
    }

    public IEnumerable<JobStatusDto> GetAllJobs() =>
        _jobs.Keys.Select(GetJob).Where(dto => dto is not null).Cast<JobStatusDto>();

    public bool TryGetOutputInfo(Guid jobId, out string? outputPath, out DownloadJobState state)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            outputPath = null;
            state = default;
            return false;
        }
        outputPath = job.OutputPath;
        state = job.State;
        return true;
    }

    public bool CancelJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        StopJobProcess(job, jobId, "cancel");
        return true;
    }

    public void DeleteJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;

        // Stop first, then remove from registry. If stopping throws/transiently fails,
        // the job remains addressable for retries.
        StopJobProcess(job, jobId, "delete");

        _jobs.TryRemove(jobId, out var removedJob);
        var outputPath = removedJob?.OutputPath ?? job.OutputPath;

        try
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete output file for job {JobId}: {Path}",
                jobId, outputPath);
        }
    }

    private void StopJobProcess(DownloadJob job, Guid jobId, string action)
    {
        // Match native Jellyfin behavior: ask ffmpeg to quit cleanly with 'q' first.
        var exitedGracefully = false;
        if (job.FfmpegProcess is { } gracefulHandle)
        {
            _logger.LogInformation("Stopping ffmpeg process with q command for {Path}", job.OutputPath);
            exitedGracefully = TryRequestFfmpegQuit(gracefulHandle, 1500);
            if (exitedGracefully)
                _logger.LogInformation("FFmpeg exited gracefully for job {JobId}.", jobId);
        }

        // Then signal cancellation so WaitForExitAsync(job.Cts.Token) unblocks.
        job.Cts?.Cancel();

        if (exitedGracefully)
            return;

        // Prefer PID kill for cross-thread reliability.
        var pid = job.FfmpegPid;
        if (pid > 0)
        {
            var exited = false;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (proc.HasExited)
                    {
                        exited = true;
                        break;
                    }

                    proc.Kill();
                    if (proc.WaitForExit(600))
                    {
                        exited = true;
                        break;
                    }
                }
                catch (ArgumentException)
                {
                    // PID no longer exists.
                    exited = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex,
                        "Kill-by-PID attempt {Attempt} on {Action} for job {JobId} (PID {Pid}) threw.",
                        attempt, action, jobId, pid);
                }

                Thread.Sleep(120);
            }

            if (exited)
            {
                _logger.LogInformation("Kill-on-{Action} confirmed ffmpeg PID {Pid} exited for job {JobId}.", action, pid, jobId);
            }
            else
            {
                _logger.LogWarning("Kill-on-{Action} could not confirm ffmpeg PID {Pid} exit for job {JobId}.", action, pid, jobId);
            }
        }

        // Belt-and-suspenders via stored Process reference in case PID wasn't set yet.
        try
        {
            if (job.FfmpegProcess is { } hardKillHandle)
            {
                hardKillHandle.Kill();
                _ = hardKillHandle.WaitForExit(1500);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex,
                "Kill-by-handle on {Action} for job {JobId} threw (process may have already exited).",
                action, jobId);
        }
    }

    private static bool TryRequestFfmpegQuit(Process process, int waitMs)
    {
        try
        {
            if (process.HasExited)
                return true;

            if (!process.StartInfo.RedirectStandardInput)
                return false;

            process.StandardInput.WriteLine("q");
            process.StandardInput.Flush();
            return process.WaitForExit(waitMs);
        }
        catch
        {
            return false;
        }
    }

    // ── Media source resolution ───────────────────────────────────────────────

    // Kept in a separate method so the JIT of StartJobAsync does not attempt to bind
    // GetPlaybackMediaSources at startup — isolating any version-mismatch failure to
    // requests that actually specify a MediaSourceId.
    private async Task<string> GetPathForMediaSourceAsync(
        MediaBrowser.Controller.Entities.BaseItem item,
        Guid itemId,
        string mediaSourceId,
        CancellationToken cancellationToken)
    {
        var sources = await _mediaSourceManager
            .GetPlaybackMediaSources(item, null, true, false, cancellationToken)
            .ConfigureAwait(false);

        var source = sources.FirstOrDefault(s => s.Id == mediaSourceId)
            ?? throw new ArgumentException(
                $"MediaSourceId '{mediaSourceId}' not found for item {itemId}.");

        return source.Path
            ?? throw new InvalidOperationException("The selected media source has no local file path.");
    }

    // ── ffmpeg execution ──────────────────────────────────────────────────────

    private async Task RunFfmpegAsync(DownloadJob job, string inputPath, CreateJobRequest request)
    {
        // NOTE: _runningCount was already incremented in StartJobAsync before this task was
        // launched.  This method is solely responsible for the matching Decrement in finally.

        var startInfo = new ProcessStartInfo
        {
            // IMediaEncoder.EncoderPath holds the path to the ffmpeg binary used by Jellyfin.
            // If your Jellyfin version names this property differently (e.g. FfmpegPath),
            // update this line to match.
            FileName = _mediaEncoder.EncoderPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };

        // Build argument list — ArgumentList handles proper escaping on all platforms,
        // preventing command injection even for paths that contain spaces or special chars.
        BuildArgumentList(startInfo.ArgumentList, inputPath, request, job.OutputPath, Config);

        _logger.LogInformation("ffmpeg args for job {JobId}: {Args}",
            job.JobId, string.Join(" ", startInfo.ArgumentList));

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        job.FfmpegProcess = process;

        try
        {
            // Register the kill callback BEFORE starting the process so there is
            // zero window between Start() and registration where a cancel can be missed.
            // If the token is already cancelled the callback fires synchronously
            // inside Register(), which is fine — the process hasn't started yet so
            // Kill() throws and is swallowed, and ThrowIfCancellationRequested() below
            // then prevents Start() from running at all.
            using var killOnCancel = job.Cts!.Token.Register(static state =>
            {
                var p = (Process)state!;
                if (TryRequestFfmpegQuit(p, 400))
                    return;

                try { p.Kill(); }
                catch { /* process already exited or not yet fully started */ }
            }, process);

            // If delete/cancel arrived before we even started, bail out immediately.
            job.Cts.Token.ThrowIfCancellationRequested();

            process.Start();
            job.FfmpegPid = process.Id;   // volatile write — immediately visible to DeleteJob
            _logger.LogInformation("Started ffmpeg PID {Pid} for job {JobId}.", process.Id, job.JobId);

            // Parse progress from ffmpeg's stderr output.
            var progressTask = ReadProgressAsync(process, job);

            await process.WaitForExitAsync(job.Cts.Token).ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                job.State = DownloadJobState.Completed;
                job.ProgressPercent = 100.0;
                _logger.LogInformation("Job {JobId} completed (exit 0).", job.JobId);
            }
            else
            {
                job.State = DownloadJobState.Failed;
                job.Error = $"ffmpeg exited with code {process.ExitCode}.";
                _logger.LogWarning("Job {JobId} failed: {Error}", job.JobId, job.Error);
            }
        }
        catch (OperationCanceledException)
        {
            job.State = DownloadJobState.Cancelled;
            _logger.LogInformation("Job {JobId} was cancelled.", job.JobId);

            // Belt-and-suspenders kill in case the token registration fired before
            // the process fully started (Register throws, callback didn't run).
            try { process.Kill(); }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Kill attempt for job {JobId} threw (process may have already exited).", job.JobId);
            }
        }
        catch (Exception ex)
        {
            job.State = DownloadJobState.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "Job {JobId} threw an unexpected exception.", job.JobId);
        }
        finally
        {
            Interlocked.Decrement(ref _runningCount);
            job.FfmpegProcess = null;
            ScheduleEviction(job);
        }
    }

    /// <summary>
    /// Reads ffmpeg's stderr and extracts the "time=" field to compute rough progress.
    /// ffmpeg writes stats like: frame=..., fps=..., time=HH:MM:SS.xx, ...
    /// </summary>
    private async Task ReadProgressAsync(Process process, DownloadJob job)
    {
        // We need the item duration to compute a percentage. Attempt to read it from the library.
        TimeSpan? duration = null;
        if (_libraryManager.GetItemById(job.ItemId) is { RunTimeTicks: not null } item)
            duration = TimeSpan.FromTicks(item.RunTimeTicks!.Value);

        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (duration is null || duration == TimeSpan.Zero) continue;

            // Look for: time=HH:MM:SS.xx
            var idx = line.IndexOf("time=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var afterKey = idx + 5;
            var available = line.Length - afterKey;
            if (available < 8) continue;  // need at least "HH:MM:SS"
            var timeStr = line.Substring(afterKey, Math.Min(11, available));
            if (TimeSpan.TryParse(timeStr, out var pos))
                job.ProgressPercent = Math.Min(99.9, pos.TotalSeconds / duration.Value.TotalSeconds * 100.0);
        }
    }

    // ── Argument building ─────────────────────────────────────────────────────

    private static void BuildArgumentList(
        ICollection<string> args,
        string inputPath,
        CreateJobRequest req,
        string outputPath,
        PluginConfiguration config)
    {
        var hwAccel = config.HardwareAcceleration;

        // ── Pre-input hardware device args (must precede -i) ─────────────────
        if (hwAccel == HardwareAccelMode.Vaapi)
        {
            args.Add("-vaapi_device");
            args.Add(config.VaapiDevice);
            // Enable VAAPI hardware decoding so frames stay on the GPU throughout,
            // avoiding the expensive CPU-decode → GPU-upload path.
            args.Add("-hwaccel");               args.Add("vaapi");
            args.Add("-hwaccel_output_format"); args.Add("vaapi");
        }

        // Input
        args.Add("-i");
        args.Add(inputPath);

        // ── Video ─────────────────────────────────────────────────────────────

        var videoEncoder = ResolveVideoEncoder(req.VideoCodec, hwAccel);
        args.Add("-c:v"); args.Add(videoEncoder);

        if (videoEncoder != "copy")
        {
            AddVideoPreset(args, videoEncoder, hwAccel);
            AddVideoQuality(args, req, hwAccel, videoEncoder);
        }

        // Scale / HW upload filter (preserve aspect ratio)
        AddScaleFilter(args, req, hwAccel, videoEncoder);

        // ── Audio ─────────────────────────────────────────────────────────────

        var audioEncoder = req.AudioCodec.ToLowerInvariant() switch
        {
            "copy" => "copy",
            "mp3"  => "libmp3lame",
            "opus" => "libopus",
            "flac" => "flac",
            _      => "aac",   // default / "aac"
        };
        args.Add("-c:a"); args.Add(audioEncoder);

        if (audioEncoder != "copy")
        {
            args.Add("-b:a"); args.Add(req.AudioBitrate.ToString());
        }

        // ── Stream mapping ────────────────────────────────────────────────────

        args.Add("-map"); args.Add("0:v:0");

        if (req.AudioStreamIndex >= 0)
        { args.Add("-map"); args.Add($"0:a:{req.AudioStreamIndex}"); }
        else
        { args.Add("-map"); args.Add("0:a:0?"); }   // "?" = ok if no audio

        // Soft subtitle mux (text-based streams only; mov_text is the MP4 container codec)
        if (req.SubtitleStreamIndex >= 0)
        {
            args.Add("-map"); args.Add($"0:s:{req.SubtitleStreamIndex}");
            args.Add("-c:s"); args.Add("mov_text");
        }

        // ── Output container ──────────────────────────────────────────────────

        // faststart moves the moov atom to the front: clients can begin playback without
        // downloading the whole file, and HTTP range requests work correctly.
        args.Add("-movflags"); args.Add("+faststart");

        // Overwrite if an output file already exists at the target path (should not happen
        // in normal operation since the path includes a new GUID, but acts as a safety net).
        args.Add("-y");

        args.Add(outputPath);
    }

    /// <summary>
    /// Maps the requested codec name and hardware acceleration mode to an ffmpeg encoder name.
    /// Returns "copy" when stream-copying. VP9 always uses the software encoder (hardware VP9
    /// encode is not widely available).
    /// </summary>
    private static string ResolveVideoEncoder(string codec, HardwareAccelMode hwAccel)
    {
        if (string.Equals(codec, "copy", StringComparison.OrdinalIgnoreCase))
            return "copy";

        bool isHevc = string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase);
        bool isVp9  = string.Equals(codec, "vp9",  StringComparison.OrdinalIgnoreCase);

        // VP9 hardware encode is not widely available — always use software.
        if (isVp9 || hwAccel == HardwareAccelMode.None)
            return isHevc ? "libx265" : isVp9 ? "libvpx-vp9" : "libx264";

        return hwAccel switch
        {
            HardwareAccelMode.Nvenc        => isHevc ? "hevc_nvenc"        : "h264_nvenc",
            HardwareAccelMode.Qsv          => isHevc ? "hevc_qsv"          : "h264_qsv",
            HardwareAccelMode.Vaapi        => isHevc ? "hevc_vaapi"        : "h264_vaapi",
            HardwareAccelMode.VideoToolbox => isHevc ? "hevc_videotoolbox" : "h264_videotoolbox",
            HardwareAccelMode.Amf          => isHevc ? "hevc_amf"          : "h264_amf",
            _                              => isHevc ? "libx265"           : "libx264",
        };
    }

    /// <summary>Adds encoder preset flag(s). Each backend uses a different flag name/vocabulary.</summary>
    private static void AddVideoPreset(ICollection<string> args, string videoEncoder, HardwareAccelMode hwAccel)
    {
        switch (hwAccel)
        {
            case HardwareAccelMode.None:
                // libvpx-vp9 does not accept -preset.
                if (videoEncoder != "libvpx-vp9")
                    { args.Add("-preset"); args.Add("superfast"); }
                break;
            case HardwareAccelMode.Nvenc:
                // NVENC: p1 (fastest) … p7 (slowest). "fast" is an alias for p4.
                args.Add("-preset"); args.Add("fast");
                break;
            case HardwareAccelMode.Qsv:
                args.Add("-preset"); args.Add("veryfast");
                break;
            case HardwareAccelMode.Amf:
                // AMF quality tiers: speed / balanced / quality
                args.Add("-quality"); args.Add("speed");
                break;
            // Vaapi and VideoToolbox have no meaningful -preset.
        }
    }

    /// <summary>
    /// Adds quality / bitrate flags. Each backend exposes a different CRF-equivalent parameter.
    /// When <see cref="CreateJobRequest.VideoBitrate"/> is set, CBR/VBR mode is used for all
    /// backends in the same way.
    /// </summary>
    private static void AddVideoQuality(
        ICollection<string> args,
        CreateJobRequest req,
        HardwareAccelMode hwAccel,
        string videoEncoder)
    {
        if (req.VideoBitrate.HasValue)
        {
            args.Add("-b:v"); args.Add(req.VideoBitrate.Value.ToString());
            return;
        }

        switch (hwAccel)
        {
            case HardwareAccelMode.Nvenc:
                // -cq: Constant Quality mode (0 = auto, 1–51 similar to CRF scale).
                args.Add("-cq"); args.Add(req.Crf.ToString());
                break;
            case HardwareAccelMode.Qsv:
                // -global_quality: ICQ (Intelligent Constant Quality) — lower = better.
                args.Add("-global_quality"); args.Add(req.Crf.ToString());
                break;
            case HardwareAccelMode.Vaapi:
                // -qp: Quantisation parameter (0–52, lower = better quality).
                args.Add("-qp"); args.Add(req.Crf.ToString());
                break;
            case HardwareAccelMode.Amf:
                // AMF constant-QP mode: set I/P/B frame quantisers independently.
                var qp = req.Crf.ToString();
                args.Add("-rc"); args.Add("cqp");
                args.Add("-qp_i"); args.Add(qp);
                args.Add("-qp_p"); args.Add(qp);
                args.Add("-qp_b"); args.Add(qp);
                break;
            case HardwareAccelMode.VideoToolbox:
                // VideoToolbox has no direct CRF equivalent; omit to use encoder defaults.
                break;
            default: // software
                if (videoEncoder == "libvpx-vp9")
                {
                    // VP9 quality mode: -crf sets quality, -b:v 0 switches to constrained-quality.
                    args.Add("-crf"); args.Add(req.Crf.ToString());
                    args.Add("-b:v"); args.Add("0");
                }
                else
                {
                    args.Add("-crf"); args.Add(req.Crf.ToString());
                }
                break;
        }
    }

    /// <summary>
    /// Adds a <c>-vf</c> filter chain for optional scaling.
    /// For VA-API, frames are already VAAPI surfaces (hwaccel_output_format=vaapi), so
    /// <c>scale_vaapi</c> is used to keep the entire pipeline on the GPU.
    /// For other backends a software <c>scale</c> filter is used.
    /// </summary>
    private static void AddScaleFilter(
        ICollection<string> args,
        CreateJobRequest req,
        HardwareAccelMode hwAccel,
        string videoEncoder)
    {
        if (videoEncoder == "copy") return;

        bool needsScale = req.MaxWidth.HasValue || req.MaxHeight.HasValue;

        if (hwAccel == HardwareAccelMode.Vaapi)
        {
            // Frames are already VAAPI surfaces; use scale_vaapi for GPU-side scaling/conversion.
            // scale_vaapi doesn't have force_original_aspect_ratio, so we replicate
            // the "decrease" behaviour with an ffmpeg conditional expression.
            string vaapiFilter;
            if (needsScale)
            {
                var w = req.MaxWidth.HasValue  ? req.MaxWidth.Value.ToString()  : "-2";
                var h = req.MaxHeight.HasValue ? req.MaxHeight.Value.ToString() : "-2";

                if (req.MaxWidth.HasValue && req.MaxHeight.HasValue)
                {
                    // Bind to the constraining dimension (preserve aspect ratio, don't upscale).
                    // Backslash-escape the commas inside the 'if' expressions for ffmpeg.
                    vaapiFilter =
                        $"scale_vaapi=" +
                        $"w='if(gt(iw/ih\\,{w}/{h})\\,{w}\\,-2)':" +
                        $"h='if(gt(iw/ih\\,{w}/{h})\\,-2\\,{h})':" +
                        $"format=nv12";
                }
                else
                {
                    vaapiFilter = $"scale_vaapi=w={w}:h={h}:format=nv12";
                }
            }
            else
            {
                // No scaling needed — just ensure pixel format is nv12 for the encoder.
                vaapiFilter = "scale_vaapi=format=nv12";
            }

            args.Add("-vf");
            args.Add(vaapiFilter);
            return;
        }

        // ── Software / other HW backends ─────────────────────────────────────
        if (!needsScale) return;

        var sw = req.MaxWidth?.ToString()  ?? "-1";
        var sh = req.MaxHeight?.ToString() ?? "-1";
        args.Add("-vf");
        args.Add($"scale={sw}:{sh}:force_original_aspect_ratio=decrease");
    }

    // ── Eviction ──────────────────────────────────────────────────────────────

    private void ScheduleEviction(DownloadJob job)
    {
        var delayMs = Config.JobRetentionMinutes * 60_000;
        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (!_jobs.TryRemove(job.JobId, out var removed)) return;

            if (!Config.KeepFilesAfterEviction)
            {
                try
                {
                    if (File.Exists(removed.OutputPath))
                        File.Delete(removed.OutputPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Eviction: could not delete {Path}", removed.OutputPath);
                }
            }

            _logger.LogDebug("Evicted job {JobId} (state={State}).", removed.JobId, removed.State);
        }, TaskScheduler.Default);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel all running jobs on server shutdown.
        foreach (var job in _jobs.Values)
            job.Cts?.Cancel();
    }
}
