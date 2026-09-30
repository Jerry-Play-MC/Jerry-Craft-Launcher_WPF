[English](README.md) | [简体中文](README-zh.md)
﻿# Jerry Craft Launcher

> 一个基于 C# .NET Framework 4.5 的 超轻量Minecraft启动器

## ✨ 功能特点
- 下载原版Minecraft
- 管理你的Minecraft实例
- 支持版本隔离、不隔离
- 目前支持Forge、NeoForge、Fabric、Quilt加载器以及OptiFine

## 🚀 如何使用

- Windows 7 用户：请确保已安装以下两个系统更新，否则启动器将无法访问网络服务：
-     安装 .NET Framework 4.5（[官方下载地址](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net45)）
-     重启电脑
-     直接运行
- Windows 8.1 / Windows 10 / Windows 11 用户：
-     ﻿双击运行即可，无需安装任何额外组件。

### 方式一：直接运行（推荐普通用户）
1. 本项目暂时处于未完成阶段，不会发布正式版程序，想要体验的请前往此页面下载此项目的原始版本：
   - [点击下载](蓝奏云网盘：https://wwbxj.lanzoul.com/b01bjnlkxa 密码:JCL)

> 💡 如果杀毒软件报错，请添加信任区，因为本启动器使用了代码压缩（Costura.Fody）且未购买数字签名。

### 方式二：自行编译运行（面向开发者）
1. 使用 `git clone` 将本仓库克隆到本地。
2. 使用 **Visual Studio 2022**（或更高版本）打开解决方案文件（`.sln`）。
3. 右键解决方案，选择 **还原 NuGet 包**（确保 Fody、Costura.Fody 等依赖下载完整）。
4. 生成即可。

## ⚠️ 当前状态与注意事项
> **开发背景**：本项目是作者在暑期两个月内独立完成的个人练习作品，旨在学习 C# 和 .NET 开发。由于作者目前仍在学习阶段，代码风格和架构设计上可能存在不足之处，**请谨慎用于生产环境或重要存档**。

## 🛠️ 技术栈与依赖
- 目标框架：.NET Framework 4.5
- 开发环境：Visual Studio 2026
- 核心库：
    Fody 6.0.0
    Costura.Fody 4.1.0

## 🙏 致谢
本项目的学习和开发过程中参考了以下优秀开源项目：
- **Plain Craft Launcher 2**：https://github.com/Hex-Dragon/PCL2
- **PCL Community Edition**：https://github.com/PCL-Community/PCL-CE
- 特别感谢 **DeepSeek** 提供的编程指导与建议。

## 发展方向
- 我们将会补全一些其他国内主流启动器的功能，并尝试开发跨平台应用。
- 日后（可能是几个月、几年，但也可能不会有。）我们会尝试开发基岩版启动器并整合到此项目

## 原项目开源仓库（未发布正式版但已停止维护）
-     https://github.com/Jerry-Play-MC/Jerry_Craft_Launcher