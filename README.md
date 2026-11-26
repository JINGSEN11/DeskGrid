<div align="center">

# DeskGrid

**面向 Windows 11（兼顾 Windows 10）的开源轻量桌面整理助手与增强型 Fluent Dock 栏**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-v0.2.0-0A84FF)](https://github.com/JINGSEN11/DeskGrid/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20%7C%2010-0078D4?logo=windows)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Zero Dependencies](https://img.shields.io/badge/Dependencies-Zero%203rd--Party-success)](#技术特点)

简体中文 | [English](README_EN.md)

</div>

---

## 简介

**DeskGrid** 基于 **.NET 10 (LTS) + WPF（原生 Fluent Theme）+ Win32 / DWM / Shell COM** 构建，运行期**零第三方 UI 框架依赖**。旨在提供原生 Win11 Fluent 毛玻璃质感、低内存常驻、零侵入可逆的桌面分区收纳与 Dock 交互体验。

## 核心特性

### 桌面分区盒子（混合管理模式）
- **虚拟盒子（Virtual Box）**：存放应用、文件、文件夹或网址的快捷引用，**不挪动磁盘原文件**，移出条目绝不删除源文件；自动按规范化路径查重拦截重复快捷方式。
- **目录映射盒子（Folder Portal Box）**：直接绑定任意本地文件夹（如 `Desktop`、`Downloads` 或项目目录），基于 `FileSystemWatcher` 实时同步变动，支持进入子目录与返回上级，删除默认移入回收站。
- **手动拖拽排序**：虚拟盒子与目录映射盒子均支持内部拖拽图标自由调整顺序（目录映射盒子通过 `CustomItemOrder` 独立持久化排序，不改动磁盘原文件）。
- **卷起 / 悬停展开**：双击标题栏或点击折叠按钮即可卷起为标题栏；鼠标悬停或拖入文件时自动平滑展开。
- **桌面友好体验**：原生 Win11 DWM Acrylic/Mica 毛玻璃、设置页平滑实时全局透明度联动（同时保留单盒 4 档通透度微调）、深浅色自动跟随系统、位置与尺寸锁定、网格与边缘吸附、`Win+D` 免疫、双击桌面空白处一键显隐所有盒子。

### 增强型 Dock 栏
- **四段式清晰布局**：`[ 快捷工具区 ] | [ 固定应用 / 文件夹区 ] | [ 当前运行中窗口区 ] | [ 极简自定义时钟 ]`。
- **便捷固定与拖拽排序**：支持直接从桌面/资源管理器拖入 `.exe`、`.lnk` 或文件夹固定，支持右键菜单「添加固定应用...」与运行区「固定到 Dock」，并支持固定区图标横向拖拽排序。
- **极简时钟与快捷电源面板**：支持 24/12 小时制、秒数与日期星期开关（实时联动生效）；点击时钟弹出深色毛玻璃快捷电源控制面板（锁定屏幕、睡眠、带确认防误触的重启与关机）。
- **内置高频快捷工具**：最左侧提供 **一键显隐系统原生桌面图标**、显隐全部盒子、新建盒子、偏好设置入口。
- **丝滑交互与高对比弹窗**：60fps 零闲置开销波浪悬停放大动效、Fluent 运行指示点、高对比深色磨砂多窗口切换弹窗（亮色壁纸下依然清晰锐利）、支持盒子 ↔ Dock 双向拖拽。
- **全屏避让与 Win11 25H2 隐藏加固**：全屏游戏/视频自动静默隐藏；支持彻底隐藏 Windows 原生任务栏（适配 Win11 25H2 Build 26200 消除底部残影）并在正常退出或异常崩溃重启时 100% 自动恢复。

## 版本更新日志

### v0.2.0
- **新增**：Dock 栏右侧极简自定义时钟（支持 24/12 小时制、秒数、日期星期开关及完整日期时间 Tooltip）。
- **新增**：点击 Dock 时钟弹出深色圆角毛玻璃「快捷电源控制面板」（锁定屏幕、睡眠、二次确认重启/关机）。
- **新增**：Dock 外部文件直接拖入固定与右键菜单「添加固定应用...」快捷入口。
- **增强**：Dock 固定区横向拖拽排序与桌面盒子内部拖拽排序（虚拟盒子与目录映射盒子均支持手动顺序持久化）。
- **增强**：虚拟盒子规范化路径自动查重（忽略大小写、末尾斜杠与引号，防止重复快捷方式）。
- **修复**：设置页透明度滑块关闭离散吸附，实现连续平滑拖动并实时联动所有已打开桌面盒子。
- **修复**：`WindowListPopup` 升级为统一深色高对比磨砂卡片，解决亮色壁纸下文字发虚问题。
- **修复**：设置页「自动隐藏原生任务栏」提示联动反向与内容溢出问题，并加固 Win11 25H2 任务栏隐藏与退出自动恢复逻辑。

### 配置管理与便携运行
- **容灾存储**：配置采用 JSON 原子写入 + 3 份滚动备份自动恢复。
- **一键迁移**：支持导出/导入 `.deskgrid` 配置文件，以及无 UAC 弹窗的开机自启设置。
- **便携模式**：在程序同级目录创建空文件 `portable.flag` 即可启用便携模式，配置自动保存至 `./data/config.json`（默认保存于 `%APPDATA%\DeskGrid\config.json`）。

## 环境要求

- **操作系统**：Windows 11 22H2+（推荐，完整 DWM Acrylic/Mica）或 Windows 10 21H2+
- **开发环境**：[.NET 10 SDK](https://dotnet.microsoft.com/download)（用于从源码编译）

## 快速开始

```powershell
# 1. 克隆仓库
git clone https://github.com/JINGSEN11/DeskGrid.git
cd DeskGrid

# 2. 编译解决方案（Release）
dotnet build DeskGrid.sln -c Release

# 3. 运行核心层自动化测试
dotnet run --project tests/DeskGrid.Tests/DeskGrid.Tests.csproj -c Release

# 4. 启动 DeskGrid
dotnet run --project src/DeskGrid.App/DeskGrid.App.csproj -c Release
```

### 发布独立绿色版（示例）

```powershell
# 依赖本机 .NET 10 桌面运行时（体积极小）
dotnet publish src/DeskGrid.App/DeskGrid.App.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64

# 或打包自包含单文件版本（无需预先安装 .NET 运行时）
dotnet publish src/DeskGrid.App/DeskGrid.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64-standalone
```

## 项目结构

```text
DeskGrid/
├── docs/
│   └── PRD.md                 # 产品需求规格文档
├── src/
│   ├── DeskGrid.App/          # WPF 桌面应用层（Fluent 样式、盒子/Dock 窗口、托盘与设置）
│   ├── DeskGrid.Core/         # 领域模型与核心服务（配置容灾、盒子/Dock 管理、网格吸附与缩放计算）
│   └── DeskGrid.Win32/        # 原生 Win32 / DWM / Shell COM 互操作层
├── tests/
│   └── DeskGrid.Tests/        # 核心层自动化测试
├── DeskGrid.sln
└── LICENSE
```

## 技术特点

- **纯原生技术栈**：仅使用 .NET 10 + WPF 原生 Fluent Theme + Win32/DWM/Shell API，无外部 NuGet 运行时依赖，易于审计与维护。
- **清晰分层架构**：核心业务逻辑（`DeskGrid.Core`）、系统底层能力（`DeskGrid.Win32`）与界面层（`DeskGrid.App`）完全解耦。

更多产品细节请参考 [docs/PRD.md](docs/PRD.md)。

## 开源协议

本项目基于 [MIT License](LICENSE) 开源，欢迎自由使用、修改与提交 Pull Request。
