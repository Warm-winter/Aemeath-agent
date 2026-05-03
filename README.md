# Aemeath Agent

Aemeath Agent 是一个面向 Windows 的桌宠式 AI 助手，基于 .NET 8、Avalonia UI、Semantic Kernel 和本地语音能力构建。项目围绕“爱弥斯 / 小爱”的角色化体验设计，提供桌宠陪伴、聊天窗口、Provider 配置、工具调用确认、长期记忆、静态知识库与语音交互等能力。

## Features

- 透明置顶桌宠窗口，支持拖拽、双击打开聊天、右键菜单、跟随鼠标、置顶、收纳托盘和退出。
- 桌宠轻量互动，包含点击反馈、气泡台词、闲置问候、大小、透明度和边缘吸附设置。
- Avalonia 聊天界面，支持会话管理、消息复制、重新回答、语音模式、长按录音和聊天背景。
- 多 Provider 管理，支持保存多个 OpenAI-compatible Provider、连接测试、模型列表获取、多模型配置和聊天页快速切换。
- 本地长期记忆，支持自动总结上下文，并在设置页查看、编辑、删除和清空。
- 内置静态知识库，覆盖爱弥斯与鸣潮相关基础资料，支持关键词命中和 AI 主动静默检索。
- 工具调用确认机制，仅对删除、覆盖、清空等高风险操作要求用户确认。
- 应用优先打开策略，用户要求打开服务时优先尝试本机应用，找不到再打开网页。
- 语音输入与唤醒词能力，集成 Windows 语音、Whisper 和 Porcupine 唤醒资源。

## Tech Stack

- .NET 8
- C#
- Avalonia UI
- CommunityToolkit.Mvvm
- Microsoft Semantic Kernel
- NAudio
- Whisper.net
- Porcupine

## Repository Layout

```text
Aemeath/
├─ Aemeath.sln
├─ Directory.Build.props
├─ Directory.Packages.props
├─ build.bat
├─ assets/
│  ├─ app.ico
│  ├─ daiji.gif
│  ├─ yidong.gif
│  ├─ dianji.gif
│  ├─ user-male.png
│  ├─ user-female.png
│  ├─ xiaoai-avatar.png
│  └─ voice/
├─ src/
│  ├─ Aemeath.Core/
│  ├─ Aemeath.Desktop/
│  ├─ Aemeath.Pet/
│  └─ Aemeath.Speech/
└─ tools/
   └─ installer.iss
```

## Requirements

- Windows 10/11 x64
- .NET SDK 8.0 or newer
- Optional: Inno Setup, if you need to build the installer

## Build

From the repository root:

```powershell
dotnet restore Aemeath.sln
dotnet build Aemeath.sln -c Debug
```

Release publish:

```powershell
dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop
```

Or use the bundled script:

```bat
build.bat
```

## Run

After publishing:

```text
publish\Aemeath.Desktop\Aemeath-agent.exe
```

During development, you can also run the desktop project directly from your IDE or with `dotnet run` where supported by the Windows desktop target.

## Runtime Binaries

This source repository intentionally does not commit generated outputs or large runtime binaries such as:

- `bin/`
- `publish/`
- project `bin/` and `obj/`
- installer output

The desktop project can optionally copy `bun.exe`, `uv.exe`, `uvw.exe`, and `uvx.exe` from the repository root `bin/` folder when those files exist. They are used by built-in MCP helpers, but `bun.exe` is larger than GitHub's normal single-file limit, so these files should be distributed through Releases, an installer package, or a local runtime cache instead of normal Git history.

The source code still builds without those files. In the app, open `设置中心 -> MCP 配置` and use `检测/下载 MCP 依赖` to download missing `uv.exe` and `bun.exe` into `%AppData%\Aemeath\tools\bin`. The downloader tries multiple China-friendly mirrors and falls back automatically when a mirror is unavailable.

## Configuration And Privacy

User settings are stored under the current Windows user profile, following the app's `%AppData%/Aemeath` convention. API keys are protected locally with Windows DPAPI where handled by the app.

The repository excludes local settings, memories, MCP server configs, logs, dumps, caches, published binaries, and environment files. Do not commit real API keys, access tokens, private conversations, local memory JSON files, or generated release folders.

## Installer

The Inno Setup script lives at:

```text
tools\installer.iss
```

Build the Release publish output first, then compile the installer script if you need a packaged Windows installer.

## Verification

Recommended checks before publishing changes:

```powershell
dotnet build Aemeath.sln -c Debug
dotnet test Aemeath.sln
```

At the moment, the solution has no dedicated test projects, so `dotnet test Aemeath.sln` mainly acts as a baseline command for future tests.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
