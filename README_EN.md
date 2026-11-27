<div align="center">

# DeskGrid

**An open-source, lightweight Windows desktop organizer and enhanced Fluent Dock for Windows 11 (and Windows 10).**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-v0.2.0-0A84FF)](https://github.com/JINGSEN11/DeskGrid/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20%7C%2010-0078D4?logo=windows)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Zero Dependencies](https://img.shields.io/badge/Dependencies-Zero%203rd--Party-success)](#architecture--highlights)

[简体中文](README.md) | English

</div>

---

## Overview

**DeskGrid** is built with **.NET 10 (LTS) + WPF (native Fluent Theme) + Win32 / DWM / Shell COM**, with **zero third-party runtime UI dependencies** and zero WinForms overhead. It delivers native Windows 11 Fluent translucency, 0% idle CPU usage, low memory footprint, and non-destructive desktop organization alongside a smooth, multi-monitor customizable Dock.

## Key Features

### Desktop Boxes (Hybrid Management)
- **Virtual Boxes**: Store references to apps, files, folders, or URLs **without moving your original files**. Removing an item never deletes the source file; duplicate paths are automatically deduplicated via path normalization.
- **Folder Portal Boxes**: Bind directly to any local directory (e.g., `Desktop`, `Downloads`, or project folders) with real-time `FileSystemWatcher` sync, subfolder navigation, safe Recycle Bin deletion, and persistent drag-to-reorder (`CustomItemOrder`) without touching files on disk.
- **Roll-Up / Hover-to-Expand**: Double-click the title bar or click the collapse button to roll up a box into its header; hover over it or drag files onto it to smoothly expand.
- **Desktop-Native Feel**: Native Win11 DWM Acrylic/Mica backdrop, smooth live global opacity slider (plus per-box 4-level opacity overrides), automatic light/dark theme sync, position & size locking, grid & edge snapping, `Win+D` immunity, and double-click empty desktop to toggle all boxes.

### Enhanced Fluent Dock
- **Clean 4-Zone Layout**: `[ Quick Tools ] | [ Pinned Apps & Folders ] | [ Running Windows ] | [ Minimal Clock ]`.
- **Multi-Monitor Support**: Choose **Primary Display Only**, **All Displays**, or **Follow Mouse Display**, with native Win32 cached monitor geometry and per-monitor mixed-DPI / negative-coordinate accuracy.
- **Overall Dock Scaling & Zero-Idle-CPU Wave Animation**: Smoothly scale the Dock from `0.70x` to `1.30x` with automatic top headroom; GPU-only `RenderTransform` 60fps magnification automatically detaches when idle.
- **Easy Pinning & Drag Reorder**: Drop `.exe`, `.lnk`, or folders directly onto the Dock, add via context menu, or drag pinned items horizontally to reorder.
- **Minimal Clock & Quick Power Menu**: Customize 24h/12h format, seconds, and date/weekday display; click the clock to open a frosted-glass quick power panel (Lock, Sleep, Restart/Shutdown with confirmation).
- **Fullscreen Awareness & Bottom-Edge Reveal**: Per-monitor silent auto-hide during fullscreen games/videos, with optional bottom-edge hotzone dwell to temporarily reveal the Dock.
- **Win11 25H2 Taskbar Guard & Safe Exit Recovery**: Hardened native Windows taskbar hiding for Win11 25H2 (Build 26200); exiting via the system tray or Settings **Safe Exit** automatically restores the native taskbar and desktop icons, with crash recovery on restart.

### Configuration & Portability
- **Resilient Storage**: Atomic JSON writes with 3 rolling backups for automatic recovery.
- **Easy Backup & Startup**: One-click `.deskgrid` config export/import and UAC-free startup registration.
- **Portable Mode**: Place an empty `portable.flag` file next to `DeskGrid.exe` to store data in `./data/config.json` (defaults to `%APPDATA%\DeskGrid\config.json`).

## Requirements

- **OS**: Windows 11 22H2+ (recommended, full DWM Acrylic/Mica) or Windows 10 21H2+ (x64)
- **SDK**: [.NET 10 SDK](https://dotnet.microsoft.com/download) (for building from source)

## Getting Started

```powershell
# 1. Clone the repository
git clone https://github.com/JINGSEN11/DeskGrid.git
cd DeskGrid

# 2. Build the solution (Release)
dotnet build DeskGrid.sln -c Release

# 3. Run automated core tests
dotnet run --project tests/DeskGrid.Tests/DeskGrid.Tests.csproj -c Release

# 4. Launch DeskGrid
dotnet run --project src/DeskGrid.App/DeskGrid.App.csproj -c Release
```

### Publishing Portable Builds

```powershell
# Framework-dependent build (smallest footprint, requires .NET 10 Desktop Runtime)
dotnet publish src/DeskGrid.App/DeskGrid.App.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64

# Self-contained compressed single-file build (no .NET runtime required)
dotnet publish src/DeskGrid.App/DeskGrid.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishReadyToRun=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o publish/win-x64-standalone
```

## Project Structure

```text
DeskGrid/
├── docs/
│   └── PRD.md                 # Product Requirements Document (Chinese)
├── src/
│   ├── DeskGrid.App/          # WPF desktop client (Fluent UI, Boxes, Dock, Settings, Tray)
│   ├── DeskGrid.Core/         # Domain models & core services (Config, Box/Dock, Multi-monitor & Edge Reveal)
│   └── DeskGrid.Win32/        # Native Win32 / DWM / Shell COM interop layer (Zero WinForms)
├── tests/
│   └── DeskGrid.Tests/        # Core automated test suite
├── DeskGrid.sln
└── LICENSE
```

## Architecture & Highlights

- **Zero External Dependencies**: Built purely on .NET 10, native WPF Fluent Theme, and Win32/DWM/Shell APIs (without WinForms) for transparency, compact single-file size, and easy auditing.
- **Clean Layer Separation**: Domain logic (`DeskGrid.Core`), OS interop (`DeskGrid.Win32`), and UI presentation (`DeskGrid.App`) are cleanly decoupled.

## License

Copyright © 2026 **Jens ([JINGSEN11](https://github.com/JINGSEN11/DeskGrid))**. All Rights Reserved.

This project is licensed under the [MIT License](LICENSE). Contributions, issues, and pull requests are welcome!
