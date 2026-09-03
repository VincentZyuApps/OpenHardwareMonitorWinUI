<p align="center">
  <img src="logo.svg" width="160" alt="Open Hardware Monitor WinUI logo">
</p>

# 🖥️ Open Hardware Monitor WinUI

> **[📖 English](README.en-us.md)**
> **[📖 简体中文（中国大陆）](README.md)**

[![Build](https://github.com/VincentZyuApps/OpenHardwareMonitorWinUI/actions/workflows/release.yml/badge.svg?branch=master)](https://github.com/VincentZyuApps/OpenHardwareMonitorWinUI/actions/workflows/release.yml)
[![Release](https://img.shields.io/github/v/release/VincentZyuApps/OpenHardwareMonitorWinUI?include_prereleases&sort=semver&logo=github&logoColor=white)](https://github.com/VincentZyuApps/OpenHardwareMonitorWinUI/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/VincentZyuApps/OpenHardwareMonitorWinUI/total?logo=github&logoColor=white)](https://github.com/VincentZyuApps/OpenHardwareMonitorWinUI/releases)
![Windows](https://img.shields.io/badge/Windows-10%202004%2B%20%7C%20x64-0078D4.svg?logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI+PHBhdGggZD0iTTAgMGgxMS4zNzd2MTEuMzcySDB6TTEyLjYyMyAwSDI0djExLjM3MkgxMi42MjN6TTAgMTIuNjIzaDExLjM3N1YyNEgweiBNMTIuNjIzIDEyLjYyM0gyNFYyNEgxMi42MjN6IiBmaWxsPSIjZmZmIi8+PC9zdmc+)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)

Open Hardware Monitor WinUI is an experimental, independent WinUI 3 desktop application for Windows. It presents hardware sensor data such as temperatures, fan speeds, voltages, loads, and clock speeds in a compact Fluent interface.

It reuses the `OpenHardwareMonitorLib` acquisition layer from the [current upstream project](https://github.com/HardwareMonitor/openhardwaremonitor), while completely rewriting the UI and configuration system. It does not read, import, or migrate the legacy WinForms application's XML configuration.

> [!WARNING]
> The current release remains in an unstable testing stage. UI layout, application behavior, and device support may change. Verify sensor readings on your own hardware and use hardware controls with care.

## ✨ Feature Highlights

### 🧭 Hardware Monitoring

The familiar hardware → sensor type → sensor hierarchy is preserved and reimplemented with a denser WinUI 3 interaction model.

![Hardware monitoring interface](docs/images/preview/preview.hardware.png)

- Expand or collapse individual levels or the entire tree; expansion state is saved in the independent configuration.
- Refresh, search, show hidden sensors, and drag to resize the current, minimum, and maximum value columns.
- Sensor menus support renaming, hiding, adding to charts, resetting minimum/maximum values, and editing parameters.
- Double-click hardware to open an independent Fluent information window; one window is reused per device, and the complete report can be copied.

### 📈 Charts

Select curves from a clear list of sensor, hardware, type, and current value fields to inspect recent trends quickly.

![Sensor charts interface](docs/images/preview/preview.graph.png)

- Up to the latest 360 samples for each sensor with a reading are retained in memory and are not persisted after the application closes.
- Display up to eight curves at once; selections are saved in the current WinUI configuration.

### 🎛️ Fan and Hardware Controls

The Controls page only shows supported channels actually exposed by the underlying hardware library. Set a software control value or restore the device's default mode.

![Fan and hardware controls interface](docs/images/preview/preview.control.png)

> [!CAUTION]
> Not every fan or device supports software control. Incorrect manual values may affect temperatures, stability, or hardware life; only use ranges confirmed to be safe.

### 🪟 Desktop Experience and Extensions

- Choose System, Light, or Dark themes, synchronized across the main and hardware information windows.
- Use the notification area, close to tray, start minimized, and display a compact always-on-top hardware status gadget.
- Record daily CSV sensor data with a configurable logging interval.
- Enable an optional local Web / JSON API that listens on `127.0.0.1:8085` by default.

## ✅ System Requirements

| Item | Requirement |
| --- | --- |
| Operating system | Windows 10 version 2004 (build 19041) or later, including Windows 11 |
| Architecture | x64 |
| Privileges | The application requests administrator privileges for low-level hardware and driver access |
| Runtime | Release ZIPs are self-contained; no separate .NET runtime is required |

Available sensors and controls depend on support from the motherboard, device, driver, and `OpenHardwareMonitorLib`.

## ⬇️ Download and Run

1. Open [Releases](https://github.com/VincentZyuApps/OpenHardwareMonitorWinUI/releases/latest).
2. Download the latest `OpenHardwareMonitor-*-win-x64.zip`; also download its `.sha256` file when verification is needed.
3. Extract the complete ZIP to a writable directory instead of running it from inside the archive.
4. Launch `OpenHardwareMonitorWinUI.exe` and accept the UAC elevation prompt.

Verify the downloaded archive's SHA-256 in PowerShell:

```powershell
Get-FileHash .\OpenHardwareMonitor-*-win-x64.zip -Algorithm SHA256
```

## ⚙️ Configuration and Portable Mode

The WinUI application uses a fresh JSON configuration that is completely independent of the legacy WinForms configuration.

| Content | Default location |
| --- | --- |
| User configuration | `%LOCALAPPDATA%\OpenHardwareMonitorWinUI\OpenHardwareMonitor.WinUI.json` |
| Portable configuration | `OpenHardwareMonitor.WinUI.json` beside `OpenHardwareMonitorWinUI.exe` |
| CSV logs | `%LOCALAPPDATA%\OpenHardwareMonitorWinUI\Logs` |

Before the first launch, place an empty `.portable` file beside the executable or pass `--portable` to enable portable configuration. This does not migrate existing settings automatically.

## 🛠️ Build from Source

Prepare Windows x64, Git, and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run:

```powershell
git clone https://github.com/VincentZyuApps/OpenHardwareMonitorWinUI.git
cd OpenHardwareMonitorWinUI
dotnet build OpenHardwareMonitor.App/OpenHardwareMonitor.App.csproj -c Debug
dotnet test OpenHardwareMonitor.App.Tests/OpenHardwareMonitor.App.Tests.csproj -c Debug
```

## 🗂️ Project Structure

| Project | Responsibility |
| --- | --- |
| `OpenHardwareMonitorLib` | Upstream hardware discovery, sensor readings, and device controls |
| `OpenHardwareMonitor.Core` | Snapshots, configuration, logging, and Web services for the WinUI application |
| `OpenHardwareMonitor.App` | Windows App SDK / WinUI 3 desktop interface |
| `OpenHardwareMonitor.App.Tests` | WinUI settings persistence and migration tests |

## 🚀 CI and Releases

- A commit message containing `[build-action]` builds a self-contained x64 ZIP and SHA-256, then uploads an Actions artifact.
- `[build-release]` on `master` also creates a version tag and GitHub Release.
- The authoritative build pipeline is [`.github/workflows/release.yml`](.github/workflows/release.yml).

## 🔗 Upstream and Licensing

This project is an independent experimental WinUI application, not an official replacement for the upstream UI. Its acquisition layer is reused from [HardwareMonitor/OpenHardwareMonitor](https://github.com/HardwareMonitor/openhardwaremonitor), which derives from the [original OpenHardwareMonitor](https://github.com/openhardwaremonitor/openhardwaremonitor).

The `OpenHardwareMonitorLib` project declares the `MPL-2.0` license; third-party components retain their respective licenses and copyright notices. This repository currently has no root license covering the entire new application, so clarify and add the project license before distributing modified builds.
