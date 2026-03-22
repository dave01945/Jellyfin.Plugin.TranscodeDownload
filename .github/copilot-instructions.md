# Project Guidelines

## Code Style
- Follow existing C# style in this repo: nullable reference types enabled, explicit access modifiers, and XML docs on public APIs.
- Keep plugin-facing classes `sealed` unless there is a clear extension requirement.
- Prefer async APIs with `CancellationToken` plumbing for long-running work.
- Keep controller actions thin and push job/process logic into services.

## Architecture
- Plugin bootstrap lives in [Plugin.cs](Plugin.cs) and [PluginServiceRegistrator.cs](PluginServiceRegistrator.cs).
- HTTP API surface is in [Controllers/TranscodeDownloadController.cs](Controllers/TranscodeDownloadController.cs).
- Job lifecycle, ffmpeg invocation, and in-memory state tracking are in [Services/TranscodeDownloadService.cs](Services/TranscodeDownloadService.cs).
- DTOs and request/response contracts are in [Models](Models) and plugin settings are in [Configuration/PluginConfiguration.cs](Configuration/PluginConfiguration.cs).
- Do not change the plugin GUID in [Plugin.cs](Plugin.cs) after deployment.

## Build and Test
- Build release artifacts with:
  - `dotnet build -c Release`
- There is currently no test project in this workspace; validate behavior by running the API endpoints against a Jellyfin instance.
- Preferred deployment workflow is via VS Code tasks in [.vscode/tasks.json](.vscode/tasks.json):
  - `Deploy Jellyfin plugin via SCP`
  - `Deploy + Restart Jellyfin + Show Logs`

## Conventions
- Preserve thread-safe job coordination patterns used in [Services/TranscodeDownloadService.cs](Services/TranscodeDownloadService.cs) (`ConcurrentDictionary`, `Interlocked`, and cancellation token wiring).
- Keep ffmpeg argument construction structured and safe (no shell string concatenation for user-provided values).
- Keep HTTP status behavior stable for async jobs (`202` while running, conflict/error mapping as implemented in controller/service).
- Be aware of environment constraints:
  - [nuget.config](nuget.config) points to Jellyfin packages on GitHub Packages and may require authenticated restore.
  - Deploy tasks assume host `jellyfin` is reachable and remote sudo is available for plugin install/restart steps.
