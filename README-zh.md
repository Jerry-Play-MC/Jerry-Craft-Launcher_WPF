[English](README.md) | [简体中文](README-zh.md)

# Jerry Craft Launcher

> 一个基于 C# .NET Framework 4.5 的超轻量 Minecraft 启动器

## ✨ 功能特点
- 下载原版 Minecraft
- 管理你的 Minecraft 实例
- 支持版本隔离、不隔离
- 目前支持 Forge、NeoForge、Fabric、Quilt 加载器以及 OptiFine
- 完全便携：无需安装、不写注册表、不写 AppData，删目录即卸载。

## 🚀 如何使用

### Windows 7 用户
请确保已安装 .NET Framework 4.5（[官方下载地址](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net45)），然后重启电脑，直接运行即可。

### Windows 8.1 / Windows 10 / Windows 11 用户
双击运行即可，无需安装任何额外组件。

### 方式一：直接运行（推荐普通用户）
1. 本项目暂时处于未完成阶段，不会发布正式版程序。

### 方式二：自行编译运行（面向开发者）
1. 使用 `git clone` 将本仓库克隆到本地。
2. 使用 **Visual Studio 2022**（或更高版本）打开解决方案文件（`.sln`）。
3. 右键解决方案，选择 **还原 NuGet 包**（确保 Fody、Costura.Fody 等依赖下载完整）。
4. 生成即可。

## ⚠️ 当前状态与注意事项

> **开发背景**：本项目是作者在暑期两个月内独立完成的个人练习作品，旨在学习 C# 和 .NET 开发。由于作者目前仍在学习阶段，代码风格和架构设计上可能存在不足之处，**请谨慎用于生产环境或重要存档**。

## 🔐 微软认证合规说明

- 仅使用微软官方 OAuth **设备代码流程**，不收集、不存储、不传输任何密码。
- 所有 OAuth 令牌（access / refresh / client token）落盘前均使用 **Windows DPAPI**（`DataProtectionScope.CurrentUser`）加密，磁盘上不存在明文令牌文件。
- 启动器自身不向注册表、AppData 或任何用户配置目录写入账号数据。所有数据位于程序自己的目录下，删除该目录即完成卸载。
- 不参与任何盗号、账号共享或黑市行为。

## 🛠️ 技术栈与依赖
- 目标框架：.NET Framework 4.5
- 开发环境：Visual Studio 2022（或更高版本）
- 核心库：
  - Fody 6.0.0
  - Costura.Fody 4.1.0

## 📦 下载

- [查看所有版本](https://github.com/Jerry-Play-MC/Jerry-Craft-Launcher_WPF/releases)
- [下载最新版本](https://github.com/Jerry-Play-MC/Jerry-Craft-Launcher_WPF/releases/latest)

## 🙏 致谢
本项目的学习和开发过程中参考了以下优秀开源项目：
- **Plain Craft Launcher 2**：https://github.com/Hex-Dragon/PCL2
- **PCL Community Edition**：https://github.com/PCL-Community/PCL-CE
- 特别感谢 **DeepSeek** 提供的编程指导与建议。

## 发展方向
- 我们将会补全一些其他国内主流启动器的功能，并尝试开发跨平台应用。
- 日后（可能是几个月、几年，但也可能不会有）我们会尝试开发基岩版启动器并整合到此项目。

## 原项目开源仓库（未发布正式版但已停止维护）
- https://github.com/Jerry-Play-MC/Jerry_Craft_Launcher