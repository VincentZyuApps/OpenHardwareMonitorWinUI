# Open Hardware Monitor WinUI

## Product Boundary

- This fork is a new Windows 10 version 2004+ x64 WinUI 3 application named `OpenHardwareMonitorWinUI.exe`.
- Keep the upstream hardware acquisition library as the source of hardware readings. Do not add the legacy WinForms UI project to the solution or release workflow.
- Preserve the familiar monitor workflow: a hierarchy of hardware, sensor type, and sensor; expand/collapse; search; compact readings; and per-sensor actions.
- Keep hardware details out of the main window. Open one independent Fluent information window per hardware device, reusing an existing window when the same device is opened again.
- Use Fluent/Win11 controls and system-aware light/dark theming. The application theme has three explicit modes: System, Light, and Dark.

## Settings And Compatibility

- The WinUI application owns a fresh JSON configuration file and never reads, imports, backs up, or migrates the legacy WinForms XML configuration.
- Persist only WinUI settings: theme, window geometry, refresh interval, hardware categories, presentation preferences, hidden sensors, selected chart sensors, and tree expansion state.
- Treat setting writes as concurrent: use atomic replacement with a unique temporary file and serialization around writes.
- The optional `.portable` marker makes the JSON file live beside the executable; otherwise use the application-local data directory.

## Build And Validation

- The solution contains `OpenHardwareMonitorLib`, `OpenHardwareMonitor.Core`, `OpenHardwareMonitor.App`, and `OpenHardwareMonitor.App.Tests`.
- Build the app with `dotnet build OpenHardwareMonitor.App/OpenHardwareMonitor.App.csproj -c Debug`.
- Run tests with `dotnet test OpenHardwareMonitor.App.Tests/OpenHardwareMonitor.App.Tests.csproj -c Debug`.
- The application manifest requests administrator privileges because hardware and driver access need elevation. Smoke-test the published executable elevated.
- Pass `--smoke-hardware-info` to open the first detected hardware information window twice; the smoke check should still produce only one detail window for that hardware.
- Pass `--smoke-hardware-info-only` for background screenshot validation; it performs the same single-instance check and then hides the main window so the detail window becomes the process main window.
- When validating UI changes, inspect a real non-minimized application window. Store any temporary screenshots outside the repository and remove them after reporting unless they are explicitly requested.

## CI And Releases

- `.github/workflows/release.yml` is the authoritative WinUI build workflow. It restores, tests, publishes a self-contained x64 app, and uploads a ZIP plus SHA-256 checksum.
- `[build-action]` in a commit message produces a downloadable Actions artifact.
- `[build-release]` on `master` additionally creates a versioned GitHub release. Keep the version fields in `Directory.Build.props` consistent; `python scripts/version/check.py` verifies them.
- Never edit the individual version fields by hand. Use `python scripts/version/bump.py <target-version>` to update every managed field together, then run `python scripts/version/bump.py <target-version> --check` and `python scripts/version/check.py` to verify the result. Example: `python scripts/version/bump.py 4.0.1-alpha.2`.
- The bump script owns the WinUI product version only. Do not use it to rewrite the independent assembly versions of the upstream hardware library, the excluded legacy WinForms project, or third-party controls.

## Temporary Files

- Keep project-specific downloads, clones, scripts, screenshots, logs, and build verification artifacts in the configured per-project temporary workspace, not in the repository.
- Do not put project-specific files in a shared cache. Shared caches are reserved for reusable SDK, package-manager, and toolchain data.
- `temp/` is intentionally ignored and may contain machine-local documentation only.
