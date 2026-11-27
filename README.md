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

**DeskGrid** 基于 **.NET 10 (LTS) + WPF（原生 Fluent Theme）+ Win32 / DWM / Shell COM** 构建，运行期**零第三方 UI 框架依赖**（且零 WinForms 依赖）。旨在提供原生 Win11 Fluent 毛玻璃质感、空闲零 CPU 占用、低内存常驻、零侵入可逆的桌面分区收纳与多屏悬浮 Dock 交互体验。

## 核心特性

### 桌面分区盒子（混合管理模式）
- **虚拟盒子（Virtual Box）**：存放应用、文件、文件夹或网址的快捷引用，**不挪动磁盘原文件**，移出条目绝不删除源文件；自动按规范化路径查重拦截重复快捷方式。
- **目录映射盒子（Folder Portal Box）**：直接绑定任意本地文件夹（如 `Desktop`、`Downloads` 或项目目录），基于 `FileSystemWatcher` 实时同步变动，支持进入子目录与返回上级，删除默认移入回收站。
- **手动拖拽排序**：虚拟盒子与目录映射盒子均支持内部拖拽图标自由调整顺序（目录映射盒子通过 `CustomItemOrder` 独立持久化排序，不改动磁盘原文件）。
- **卷起 / 悬停展开**：双击标题栏或点击折叠按钮即可卷起为标题栏；鼠标悬停或拖入文件时自动平滑展开。
- **桌面友好体验**：原生 Win11 DWM Acrylic/Mica 毛玻璃、设置页平滑实时全局透明度联动（同时保留单盒 4 档通透度微调）、深浅色自动跟随系统、位置与尺寸锁定、网格与边缘吸附、`Win+D` 免疫、双击桌面空白处一键显隐所有盒子。

### 增强型悬浮 Dock 栏
- **四段式清晰布局**：`[ 快捷工具区 ] | [ 固定应用 / 文件夹区 ] | [ 当前运行中窗口区 ] | [ 极简自定义时钟 ]`。
- **多显示器显示模式**：支持 **仅主显示器**、**所有显示器**、**跟随鼠标所在屏幕** 三种模式，完整适配混合 DPI 与负坐标副屏，彻底消除跨屏重入与位置漂移。
- **Dock 整体缩放与丝滑波浪动效**：支持 `0.70x–1.30x` 连续无级缩放并自动预留顶部放大安全空间；采用纯 GPU `RenderTransform` 60fps 波浪放大，鼠标离开静止后自动注销渲染监听，空闲 0% CPU 占用。
- **便捷固定与拖拽排序**：支持直接从桌面/资源管理器拖入 `.exe`、`.lnk` 或文件夹固定，支持右键菜单「添加固定应用...」与运行区「固定到 Dock」，并支持固定区图标横向拖拽排序。
- **极简时钟与快捷电源面板**：支持 24/12 小时制、秒数与日期星期开关（实时联动生效）；点击时钟弹出深色毛玻璃快捷电源控制面板（锁定屏幕、睡眠、带确认防误触的重启与关机）。
- **内置高频快捷工具**：最左侧提供 **一键显隐系统原生桌面图标**、显隐全部盒子、新建盒子、偏好设置入口。
- **全屏避让与底边热区呼出**：全屏游戏/视频按显示器独立静默隐藏；开启「底边热区临时呼出」后，鼠标移至屏幕最底边停顿片刻即可临时滑出 Dock，离开后自动收回。
- **Win11 25H2 隐藏加固与安全退出恢复**：支持彻底隐藏 Windows 原生任务栏（适配 Win11 25H2 Build 26200 消除底部残影），通过系统托盘或设置页左下角「**安全退出**」关闭时自动恢复系统原生任务栏与桌面图标，异常崩溃重启亦会自动检测并恢复。

## 版本更新日志

### v0.2.0
- **新增**：Dock 多显示器显示模式（仅主显示器 / 所有显示器 / 跟随鼠标所在屏幕），原生 Win32 缓存显示器几何与每屏 DPI，完整支持异构 DPI 与负坐标副屏。
- **新增**：Dock 栏整体缩放滑块（`0.70x–1.30x` 连续平滑实时生效）及自动顶部安全余量计算。
- **新增**：全屏隐藏时「底边热区临时呼出 Dock」功能，支持防误触停顿判定、弹窗/菜单驻留保护与多屏独立状态机。
- **新增**：Dock 栏右侧极简自定义时钟（支持 24/12 小时制、秒数、日期星期开关及完整日期时间 Tooltip）与深色圆角毛玻璃「快捷电源控制面板」（锁定屏幕、睡眠、二次确认重启/关机）。
- **新增**：Dock 外部文件直接拖入固定、右键菜单「添加固定应用...」、Dock 固定区与桌面盒子（虚拟/目录映射）内部手动拖拽排序及持久化。
- **新增**：设置窗口升级为侧边导航布局（外观与桌面 / Dock 栏 / 配置与备份 / 关于与支持），左下角新增独立「安全退出」入口；通过托盘或设置页安全退出时自动恢复原生任务栏与桌面图标。
- **新增**：「关于与支持」页面，展示 MIT 开源与版权声明、GitHub 主页入口，以及带纯白圆角静区的高清微信赞赏码与小红书二维码（支持点击全窗口居中放大预览，按 `Esc` 或点击任意处关闭）。
- **优化与修复**：
  - 根治多显示器切换定位死循环、自身多屏 Dock 窗口递归追踪与后台卡顿；图标提取改为后台 STA 线程异步加载 + LRU 缓存，Dock 空闲时 CPU 占用归零。
  - 虚拟盒子规范化路径自动查重（忽略大小写、末尾斜杠与引号）；设置页滑块（透明度、Dock 缩放、最大放大倍率）均支持无吸附连续平滑拖动并实时联动。
  - `WindowListPopup` 升级为统一深色高对比磨砂卡片，解决亮色壁纸下文字发虚问题。
  - 修复设置页深色模式侧边栏对比度与任务栏隐藏提示联动反向问题，加固 Win11 25H2 任务栏隐藏与退出自动恢复逻辑，并移除冗余 WinForms 依赖大幅精简单文件体积。

### 配置管理与便携运行
- **容灾存储**：配置采用 JSON 原子写入 + 3 份滚动备份自动恢复。
- **一键迁移**：支持导出/导入 `.deskgrid` 配置文件，以及无 UAC 弹窗的开机自启设置。
- **便携模式**：在程序同级目录创建空文件 `portable.flag` 即可启用便携模式，配置自动保存至 `./data/config.json`（默认保存于 `%APPDATA%\DeskGrid\config.json`）。

## 环境要求

- **操作系统**：Windows 11 22H2+（推荐，完整 DWM Acrylic/Mica）或 Windows 10 21H2+（64 位）
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

### 发布瘦身单文件绿色版（示例）

```powershell
# 依赖本机 .NET 10 桌面运行时（体积极小）
dotnet publish src/DeskGrid.App/DeskGrid.App.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64

# 打包自包含压缩单文件版本（无需预先安装 .NET 运行时，Linux 跨平台编译可追加 -p:EnableWindowsTargeting=true）
dotnet publish src/DeskGrid.App/DeskGrid.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishReadyToRun=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o publish/win-x64-standalone
```

## 项目结构

```text
DeskGrid/
├── docs/
│   └── PRD.md                 # 产品需求规格文档
├── src/
│   ├── DeskGrid.App/          # WPF 桌面应用层（Fluent 样式、盒子/Dock 窗口、托盘与设置）
│   ├── DeskGrid.Core/         # 领域模型与核心服务（配置容灾、盒子/Dock 管理、多屏定位与热区状态机）
│   └── DeskGrid.Win32/        # 原生 Win32 / DWM / Shell COM 互操作层（零 WinForms 依赖）
├── tests/
│   └── DeskGrid.Tests/        # 核心层自动化测试
├── DeskGrid.sln
└── LICENSE
```

## 技术特点

- **纯原生零冗余技术栈**：仅使用 .NET 10 + WPF 原生 Fluent Theme + Win32/DWM/Shell API，无外部 NuGet 运行时依赖、无 WinForms 冗余依赖，体积精简且易于审计与维护。
- **清晰分层架构**：核心业务逻辑（`DeskGrid.Core`）、系统底层能力（`DeskGrid.Win32`）与界面层（`DeskGrid.App`）完全解耦。

更多产品细节请参考 [docs/PRD.md](docs/PRD.md)。

## 版权与开源协议

Copyright © 2026 **Jens ([JINGSEN11](https://github.com/JINGSEN11/DeskGrid))**. All Rights Reserved.

本项目基于 [MIT License](LICENSE) 开源发布，纯免费、无广告、无后台联网收集，欢迎自由使用、修改与提交 Pull Request。
