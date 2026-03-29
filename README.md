# Jellyfin Transcode Download Plugin

A Jellyfin server plugin that exposes a REST API to transcode a library item in the background and download the result as an MP4 file.

## What This Plugin Does

- Starts ffmpeg transcode jobs through an authenticated Jellyfin API endpoint.
- Tracks each job in memory with progress, state, and error details.
- Lets clients poll job status and download the output file when ready.
- Supports optional GPU acceleration (NVENC, QSV, VA-API, VideoToolbox, AMF).
- Cleans up jobs automatically after a retention window.

## Requirements

- Jellyfin server compatible with plugin target ABI (`10.10.0.0` in `meta.json`).
- .NET SDK for local builds (`net9.0` target in the project file).
- ffmpeg available to Jellyfin (the plugin uses Jellyfin's configured ffmpeg path).
- For hardware acceleration, host GPU drivers/runtime must be correctly installed and accessible.

## Build

```bash
dotnet build -c Release
```

Build output:

- `bin/Release/net9.0/Jellyfin.Plugin.TranscodeDownload.dll`
- `bin/Release/net9.0/meta.json`

## Install / Deploy

Copy both the plugin DLL and `meta.json` into a Jellyfin plugin folder named with plugin name and version, for example:

```text
/var/lib/jellyfin/plugins/TranscodeDownload_1.0.0.0/
```

Then restart Jellyfin.

This repository includes VS Code tasks that automate build + SCP deployment:

- `Deploy Jellyfin plugin via SCP`
- `Deploy + Restart Jellyfin + Show Logs`

## Configuration (Jellyfin Dashboard)

Open Dashboard -> Plugins -> TranscodeDownload.

| Setting | Default | Description |
|---|---:|---|
| Max Concurrent Jobs | 2 | Maximum number of ffmpeg jobs running at once. New requests above this limit return HTTP 429. |
| Output Directory | (empty) | If empty, uses `{TranscodePath}/downloads`. |
| Job Retention Minutes | 60 | Time before completed/failed jobs are evicted from in-memory registry. |
| Keep Files After Eviction | false | If false, output files are deleted on eviction. |
| Hardware Acceleration | None | Encoder backend: `None`, `Nvenc`, `Qsv`, `Vaapi`, `VideoToolbox`, `Amf`. |
| VA-API Device | `/dev/dri/renderD128` | Only used when Hardware Acceleration is `Vaapi`. |

## API Overview

All routes are under:

```text
/Plugins/TranscodeDownload
```

Authentication is required (`[Authorize]`), so use a valid Jellyfin token (for example `X-Emby-Token`).

### 1) Create Job

`POST /Plugins/TranscodeDownload/jobs`

Starts a background transcode and returns `202 Accepted` with a generated `jobId`.

Possible responses:

- `202 Accepted`: job created
- `400 Bad Request`: invalid payload
- `404 Not Found`: item or media source not found
- `429 Too Many Requests`: concurrent job limit reached

Example:

```bash
curl -X POST "$BASE/Plugins/TranscodeDownload/jobs" \
  -H "Content-Type: application/json" \
  -H "X-Emby-Token: $API_KEY" \
  -d '{
    "itemId": "11111111-2222-3333-4444-555555555555",
    "videoCodec": "h264",
    "crf": 23,
    "maxWidth": 1920,
    "maxHeight": 1080,
    "audioCodec": "aac",
    "audioBitrate": 128000,
    "audioStreamIndex": -1,
    "subtitleStreamIndex": -1
  }'
```

### 2) List Jobs

`GET /Plugins/TranscodeDownload/jobs`

Returns all currently tracked jobs.

### 3) Get Job Status

`GET /Plugins/TranscodeDownload/jobs/{id}`

Returns a single job status with fields including:

- `state`: `Queued`, `Running`, `Completed`, `Failed`, `Cancelled`
- `progressPercent`
- `outputSizeBytes` (when completed)
- `downloadUrl`
- `error` (when failed)

### 4) Download Output File

`GET /Plugins/TranscodeDownload/jobs/{id}/file`

Behavior depends on job state:

- `202 Accepted`: job is still `Queued` or `Running`
- `200 OK`: MP4 file stream when `Completed`
- `409 Conflict`: job is `Failed` or `Cancelled`
- `404 Not Found`: unknown job id

The endpoint supports HTTP range requests for resumable downloads.

### 5) Delete Job

`DELETE /Plugins/TranscodeDownload/jobs/{id}`

Cancels running ffmpeg (if needed), deletes output file, and removes job from registry.

Responses:

- `204 No Content`: deleted
- `404 Not Found`: unknown job id

## CreateJobRequest Fields

| Field | Type | Default | Notes |
|---|---|---|---|
| `itemId` | `Guid` | required | Jellyfin library item ID. |
| `mediaSourceId` | `string?` | null | Select specific media source/version if needed. |
| `videoCodec` | `string` | `h264` | `h264`, `hevc`, `vp9`, or `copy`. |
| `videoBitrate` | `int?` | null | If set, bitrate mode is used; otherwise CRF mode. |
| `crf` | `int` | `23` | Quality target (lower is higher quality). |
| `maxWidth` | `int?` | null | Max output width while preserving aspect ratio. |
| `maxHeight` | `int?` | null | Max output height while preserving aspect ratio. |
| `audioCodec` | `string` | `aac` | `aac`, `mp3`, `opus`, `flac`, or `copy`. |
| `audioBitrate` | `int` | `128000` | Ignored when `audioCodec=copy`. |
| `audioStreamIndex` | `int` | `-1` | `-1` means first audio stream. |
| `subtitleStreamIndex` | `int` | `-1` | `-1` disables subtitles. Text subtitles are muxed as `mov_text`. |

## Typical Client Flow

1. `POST /jobs` to create a job.
2. Poll `GET /jobs/{id}` until `state == Completed` or terminal failure.
3. Download from `GET /jobs/{id}/file`.
4. Optionally `DELETE /jobs/{id}` for early cleanup.

## Operational Notes

- Job state is in memory and is not persisted across Jellyfin restarts.
- Only local file paths are supported for transcode input.
- Subtitle muxing expects text-based subtitle streams for MP4 (`mov_text`).
- For VA-API mode, ensure Jellyfin has access to the configured render device.

## Development Notes

- Plugin entrypoint: `Plugin.cs`
- Service registration: `PluginServiceRegistrator.cs`
- API controller: `Controllers/TranscodeDownloadController.cs`
- Core job engine: `Services/TranscodeDownloadService.cs`
- Config model/UI: `Configuration/PluginConfiguration.cs`, `Configuration/configPage.html`

If restore fails on Jellyfin package feeds, verify credentials/configuration in `nuget.config`.

## Secret Scanning

This repository now includes secret scanning with gitleaks in both local commits and CI:

- Local pre-commit hook via `.pre-commit-config.yaml` (scans staged changes).
- CI workflow: `.github/workflows/secret-scan.yml` (scans full git history on `push` to `main` and on `pull_request`).

To enable local commit scanning:

```bash
pip install pre-commit
pre-commit install
```

To run a full local scan manually:

```bash
docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:latest git /repo --redact --verbose
```
