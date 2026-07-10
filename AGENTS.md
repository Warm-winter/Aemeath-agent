# Repository Guidelines

## Project Structure & Module Organization

`Aemeath.sln` is a Windows-only .NET 8 solution. Source is under `src/`: `Aemeath.Core` owns AI, settings, memory, and MCP persistence; `Aemeath.Desktop` contains Avalonia windows, shared styles, dialogs, Markdown, chat sessions, and attachment rendering; `Aemeath.Pet` owns desktop-pet animation and menus; `Aemeath.Speech` handles recording and recognition. Tests live in `tests/Aemeath.Desktop.Tests`; dated debugging reports live in `memory/`. Store images, GIFs, fonts, and license notices in `assets/`; installer sources belong in `tools/installer.iss`. Never edit generated `bin/`, `obj/`, `publish/`, or installer output.

## Build, Test, and Development Commands

Use Windows 10/11 x64 with the .NET 8 SDK.

- `dotnet restore Aemeath.sln` restores centrally managed packages.
- `dotnet build Aemeath.sln -c Debug` compiles the development build.
- `dotnet run --project src/Aemeath.Desktop/Aemeath.Desktop.csproj` launches the app.
- `dotnet test Aemeath.sln -c Debug` runs xUnit/Avalonia Headless tests.
- `dotnet build Aemeath.sln -c Release` validates optimized output.
- `build.bat --no-pause` publishes self-contained `win-x64` files.
- `git diff --check` detects whitespace errors.

## Coding Style & UI Conventions

Use four spaces, file-scoped namespaces, nullable references, implicit usings, .NET naming, and `Async` suffixes. Keep Avalonia pairs as `Name.axaml`/`Name.axaml.cs`; define package versions only in `Directory.Packages.props`. Reuse `AemeathTheme.axaml`, `AemeathControls.axaml`, and `AemiUi` tokens; screen-local hex colors are prohibited.

Every non-pet window uses `WindowDecorations="BorderOnly"` with `AemeathTitleBar`; modal title bars expose close only. Interactive controls require accessible names and visible focus. Use `DialogService` for modal flows. Settings navigation uses `SettingsPageId`, never `SelectedIndex`; Provider, Computer Control, and MCP dirty state must compare normalized snapshots, and failed saves must not navigate. Chat text and `ChatMessageRecord.Attachments` are persisted separately; render images through `AttachmentThumbnailCache` and files through compact cards. Keep message actions visible. Pet GIFs must remain within 256 px with transparent corners, and menus are built through `PetWindow.BuildContextMenu`.

## Testing, Commits & Security

Name tests `<Type>Tests.cs` and methods `Method_State_ExpectedResult`. Cover persistence compatibility, dangerous confirmations, three-way unsaved decisions, responsive layouts, accessibility, attachment fallback, title-bar state, and pet transparency/menu order. Before review, run all commands above. Use concise Chinese action-led commits. PRs must describe visible behavior and validation, with screenshots for UI changes. Never commit API keys, local chats/settings/memories, logs, dumps, runtime binaries, or `%AppData%/Aemeath` data; add third-party notices under `assets/notices/`.
