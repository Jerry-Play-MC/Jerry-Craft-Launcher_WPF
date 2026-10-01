[English](README.md) | [简体中文](README-zh.md)
# Jerry Craft Launcher

> An ultra-lightweight Minecraft launcher built on C# .NET Framework 4.5

## ✨ Features
- Download vanilla Minecraft
- Manage your Minecraft instances
- Support version isolation and non-isolation
- Currently supports Forge, NeoForge, Fabric, Quilt loaders, and OptiFine

## 🚀 How to Use

- Windows 7 users: Please make sure the following two system updates are installed, otherwise the launcher will not be able to access network services:
  - Install .NET Framework 4.5 ([official download link](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net45))
  - Restart your computer
  - Run directly
- Windows 8.1 / Windows 10 / Windows 11 users:
  - Double-click to run. No additional components are required.

### Option 1: Run directly (recommended for regular users)
1. This project is currently unfinished and no official release will be published for now.

### Option 2: Build and run by yourself (for developers)
1. Use `git clone` to clone this repository locally.
2. Open the solution file (`.sln`) with **Visual Studio 2022** (or later).
3. Right-click the solution and select **Restore NuGet Packages** (make sure dependencies such as Fody and Costura.Fody are fully downloaded).
4. Build it.

## ⚠️ Current Status and Notes
> **Development background**: This project is a personal practice work independently completed by the author within two months during summer vacation, aiming to learn C# and .NET development. Since the author is still in the learning stage, there may be shortcomings in code style and architecture design. **Please use it with caution in production environments or for important saves.**

## 🔐 Microsoft Authentication Compliance

- Uses the official Microsoft OAuth **device code flow** only. No password is ever collected, stored, or transmitted.
- All OAuth tokens (access / refresh / client token) are encrypted at rest with **Windows DPAPI** (`DataProtectionScope.CurrentUser`) before being written to disk. No plaintext token file is produced.
- The launcher itself does not write to the Windows registry, AppData, or any other user-profile location for account data. All launcher data lives under its own program directory and is removed by deleting that directory.
- Not involved in any account theft, credential sharing, or black-market activity.

## 🛠️ Tech Stack and Dependencies
- Target framework: .NET Framework 4.5
- Development environment: Visual Studio 2026
- Core libraries:
  - Fody 6.0.0
  - Costura.Fody 4.1.0

## 🙏 Acknowledgements
The learning and development of this project referred to the following excellent open-source projects:
- **Plain Craft Launcher 2**: https://github.com/Hex-Dragon/PCL2
- **PCL Community Edition**: https://github.com/PCL-Community/PCL-CE
- Special thanks to **DeepSeek** for programming guidance and suggestions.

## Roadmap
- We will complete some features of other mainstream Chinese launchers and try to develop cross-platform applications.
- In the future (maybe a few months, a few years, or maybe never), we will try to develop a Bedrock Edition launcher and integrate it into this project.

## Original Open-Source Repository (no official release yet, but maintenance has stopped)
- https://github.com/Jerry-Play-MC/Jerry_Craft_Launcher