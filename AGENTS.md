# Open Hardware Monitor WinUI Agent Guide

# English

## Product Boundary

- This fork is an independent Windows 10 version 2004+ x64 WinUI 3 application named `OpenHardwareMonitorWinUI.exe`.
- Reuse the upstream hardware acquisition library for readings; do not add the legacy WinForms UI project to the solution or release workflow.
- Preserve the familiar compact monitor workflow: hardware/sensor hierarchy, expand/collapse, search, readings, and per-sensor actions.
- Open hardware details in one reusable independent Fluent window per device; keep them out of the main window.
- Use Fluent/Windows 11 controls and support explicit System, Light, and Dark theme modes.

## Settings And Compatibility

- Own a fresh WinUI JSON configuration; never read, import, back up, or migrate the legacy WinForms XML configuration.
- Persist only WinUI settings: theme, window geometry, refresh interval, categories, presentation, hidden sensors, chart selections, and expansion state.
- Serialize concurrent writes and replace settings atomically with a unique temporary file.
- Store settings beside the executable when `.portable` exists; otherwise use the application-local data directory.

## Build And Validation

- The solution contains `OpenHardwareMonitorLib`, `OpenHardwareMonitor.Core`, `OpenHardwareMonitor.App`, and `OpenHardwareMonitor.App.Tests`.
- Build with `dotnet build OpenHardwareMonitor.App/OpenHardwareMonitor.App.csproj -c Debug`.
- Test with `dotnet test OpenHardwareMonitor.App.Tests/OpenHardwareMonitor.App.Tests.csproj -c Debug`.
- The manifest requests administrator privileges for hardware and driver access; smoke-test published executables elevated.
- Use `--smoke-hardware-info` to verify repeated opening creates one detail window per device.
- Use `--smoke-hardware-info-only` for background screenshot validation with the main window hidden.
- Inspect a real non-minimized window for UI validation and keep temporary screenshots outside the repository unless explicitly requested.

## CI And Releases

- `.github/workflows/release.yml` is authoritative and restores, tests, publishes self-contained x64 output, and uploads ZIP plus SHA-256 files.
- `[build-action]` creates a downloadable Actions artifact; `[build-release]` on `master` also creates a versioned release.
- Publish every release as non-draft, non-prerelease, and Latest, including `alpha`, `beta`, and other suffixed versions.
- Keep product version fields consistent; `python scripts/version/check.py` verifies them.
- Never edit managed version fields manually. Use `python scripts/version/bump.py <version>`, then rerun it with `--check` and run `check.py`.
- The bump script owns only the WinUI product version, not upstream library, excluded WinForms, or third-party assembly versions.

## Temporary Files

- Keep project-specific downloads, clones, scripts, screenshots, logs, builds, and validation output in the configured project temporary workspace.
- Never place project-specific files in shared caches; shared caches are only for reusable SDKs, packages, and toolchains.
- `temp/` is ignored and may contain machine-local documentation only.

---

# 中文

## 产品边界

- 本 Fork 是独立的 Windows 10 2004+ x64 WinUI 3 应用，程序名为 `OpenHardwareMonitorWinUI.exe`。
- 硬件读数复用上游采集库；不要把旧 WinForms UI 项目加入解决方案或发布流程。
- 保留熟悉的紧凑监控流程：硬件/传感器层级、展开折叠、搜索、读数和单传感器操作。
- 硬件详情不进入主窗口；每台设备使用一个可复用的独立 Fluent 详情窗口。
- 使用 Fluent/Windows 11 控件，并明确支持跟随系统、浅色、深色三种主题模式。

## 设置与兼容性

- WinUI 使用全新的独立 JSON 配置；不得读取、导入、备份或迁移旧 WinForms XML 配置。
- 只持久化 WinUI 设置：主题、窗口尺寸位置、刷新间隔、类别、显示偏好、隐藏传感器、图表选择和展开状态。
- 并发写入必须串行化，并通过唯一临时文件原子替换配置。
- 存在 `.portable` 时配置放在可执行文件旁，否则使用应用本地数据目录。

## 构建与验证

- 解决方案包含 `OpenHardwareMonitorLib`、`OpenHardwareMonitor.Core`、`OpenHardwareMonitor.App` 和 `OpenHardwareMonitor.App.Tests`。
- 构建命令：`dotnet build OpenHardwareMonitor.App/OpenHardwareMonitor.App.csproj -c Debug`。
- 测试命令：`dotnet test OpenHardwareMonitor.App.Tests/OpenHardwareMonitor.App.Tests.csproj -c Debug`。
- 清单因硬件和驱动访问请求管理员权限；发布版冒烟测试必须提权运行。
- 使用 `--smoke-hardware-info` 验证同一设备重复打开时只存在一个详情窗口。
- 使用 `--smoke-hardware-info-only` 隐藏主窗口并进行后台截图验证。
- UI 验证必须检查真实且未最小化的窗口；除非明确要求，临时截图应放在仓库外。

## CI 与发布

- `.github/workflows/release.yml` 是权威流程：还原、测试、发布自包含 x64 程序，并上传 ZIP 与 SHA-256 文件。
- `[build-action]` 生成可下载的 Actions Artifact；`master` 上的 `[build-release]` 还会创建版本化 Release。
- 所有 Release 一律设为非草稿、非 Pre-release 和 Latest，包括带 `alpha`、`beta` 等后缀的版本。
- 产品版本字段必须一致，并由 `python scripts/version/check.py` 校验。
- 禁止手改受管版本字段；使用 `python scripts/version/bump.py <版本>`，再运行其 `--check` 和 `check.py`。
- bump 脚本只管理 WinUI 产品版本，不修改上游库、已排除的 WinForms 或第三方程序集版本。

## 临时文件

- 项目相关下载、克隆、脚本、截图、日志、构建与验证产物必须放在配置好的项目临时工作区。
- 不得把项目专用文件放入共享缓存；共享缓存只用于可复用 SDK、软件包和工具链。
- `temp/` 已被忽略，只允许存放本机文档。
