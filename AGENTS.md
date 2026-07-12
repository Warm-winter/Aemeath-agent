# Repository Guidelines

## Project Structure & Module Organization

`Aemeath.sln` is a Windows-only .NET 8 solution. `src/Aemeath.Core` owns AI, settings, memory, and MCP persistence; `src/Aemeath.Desktop` contains Avalonia windows, styles, dialogs, Markdown, chat sessions, and attachment rendering; `src/Aemeath.Pet` owns desktop-pet animation and menus; `src/Aemeath.Speech` handles recording and recognition. Tests live in `tests/Aemeath.Desktop.Tests`, debugging reports in `memory/`, assets/notices in `assets/`, and installer sources in `tools/installer.iss`. Never edit generated `bin/`, `obj/`, `publish/`, or installer output.

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

Use four spaces, file-scoped namespaces, nullable references, implicit usings, .NET naming, and `Async` suffixes. Keep Avalonia pairs as `Name.axaml`/`Name.axaml.cs`; define package versions only in `Directory.Packages.props`. Reuse `AemeathTheme.axaml`, `AemeathControls.axaml`, and `AemiUi`; screen-local hex colors are prohibited.

Every non-pet window uses `WindowDecorations="BorderOnly"` with `AemeathTitleBar`; `WindowsWindowFrame` suppresses the DWM border while preserving edge resizing. Controls require accessible names and visible focus. Use `DialogService` for modal flows; three-way unsaved dialogs hide duplicate close controls. Settings navigation uses `SettingsPageId` and explicitly synchronizes selection with `SettingsContentHost.Content`; never restore `SelectedIndex` or `SelectedItem.Tag` bindings. Provider, Computer Control, and MCP dirty state compares normalized snapshots, and failed saves must not navigate. Feedback uses `AemeathToastHost` with 2.5-second success and 4.5-second error durations.

Chat `SplitView` owns its outer margin: use a transparent pane, a 240 px card, a 12 px gap, and message rows bound to `MessagesPanel`. Provider wide panes and card lists stretch vertically; narrow lists stay capped. Pet menu headers use `pet-menu-item`; actions such as `????` remain top-level.

## Testing, Commits & Security

Name tests `<Type>Tests.cs` and methods `Method_State_ExpectedResult`. Cover persistence compatibility, confirmations, unsaved decisions, responsive layouts, accessibility, attachment fallback, title-bar state, chat clipping, provider alignment, and pet transparency/menu order. Before review, run all commands above and a real-HWND frame smoke. Use concise Chinese action-led commits. PRs must describe visible behavior and validation, with screenshots for UI changes. Never commit API keys, local chats/settings/memories, logs, dumps, runtime binaries, or `%AppData%/Aemeath` data; put third-party notices under `assets/notices/`.
