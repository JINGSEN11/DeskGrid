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

**DeskGrid** is built with **.NET 10 (LTS) + WPF (native Fluent Theme) + Win32 / DWM / Shell COM**, with **zero third-party runtime UI dependencies**. It delivers native Windows 11 Fluent translucency, low memory footprint, and non-destructive desktop organization alongside a smooth, customizable Dock.

## Key Features

### Desktop Boxes (Hybrid Management)
- **Virtual Boxes**: Store references to apps, files, folders, or URLs **without moving your original files**. Removing an item never deletes the source file.
- **Folder Portal Boxes**: Bind directly to any local directory (e.g., `Desktop`, `Downloads`, or project folders) with real-time `FileSystemWatcher` sync, subfolder navigation, and safe Recycle Bin deletion.
- **Roll-Up / Hover-to-Expand**: Double-click the title bar or click the collapse button to roll up a box into its header; hover over it or drag files onto it to smoothly expand.
- **Desktop-Native Feel**: Native Win11 DWM Acrylic/Mica backdrop, automatic light/dark theme sync, position & size locking, grid & edge snapping, `Win+D` immunity, and double-click empty desktop to toggle all boxes.

### Enhanced Fluent Dock
- **Clean 3-Zone Layout**: `[ Quick Tools ] | [ Pinned Apps & Folders ] | [ Running Windows ]`.
- **Built-In Quick Actions**: One-click toggle for **native Windows desktop icons**, toggle all boxes, create new box, and open preferences.
- **Fluid Interaction**: 60fps zero-idle-cost wave magnification on hover, Fluent running indicators, lightweight multi-window picker popup, and two-way drag & drop between Boxes and the Dock.
- **Fullscreen Awareness & Safety Guard**: Automatically hides during fullscreen games/videos; optional native taskbar hiding with guaranteed automatic restoration on exit or crash recovery.

### Configuration & Portability
- **Resilient Storage**: Atomic JSON writes with 3 rolling backups for automatic recovery.
- **Easy Backup & Startup**: One-click `.deskgrid` config export/import and UAC-free startup registration.
- **Portable Mode**: Place an empty `portable.flag` file next to `DeskGrid.exe` to store data in `./data/config.json` (defaults to `%APPDATA%\DeskGrid\config.json`).

## Requirements

- **OS**: Windows 11 22H2+ (recommended, full DWM Acrylic/Mica) or Windows 10 21H2+
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

# Self-contained single-file build (no .NET runtime required)
dotnet publish src/DeskGrid.App/DeskGrid.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64-standalone
```

## Project Structure

```text
DeskGrid/
├── docs/
│   └── PRD.md                 # Product Requirements Document (Chinese)
├── src/
│   ├── DeskGrid.App/          # WPF desktop client (Fluent UI, Boxes, Dock, Settings, Tray)
│   ├── DeskGrid.Core/         # Domain models & core services (Config, Box/Dock managers, snapping)
│   └── DeskGrid.Win32/        # Native Win32 / DWM / Shell COM interop layer
├── tests/
│   └── DeskGrid.Tests/        # Core automated test suite
├── DeskGrid.sln
└── LICENSE
```

## Architecture & Highlights

- **Zero External Dependencies**: Built purely on .NET 10, native WPF Fluent Theme, and Win32/DWM/Shell APIs for transparency, small binary size, and easy auditing.
- **Clean Layer Separation**: Domain logic (`DeskGrid.Core`), OS interop (`DeskGrid.Win32`), and UI presentation (`DeskGrid.App`) are cleanly decoupled.

## License

This project is licensed under the [MIT License](LICENSE). Contributions, issues, and pull requests are welcome!
