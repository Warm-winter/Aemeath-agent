# AGENTS.md
Repository guidance for autonomous coding agents working in `E:\Aemeath`.

## 1) Project Snapshot
- Stack: .NET 8, C#, Avalonia UI, CommunityToolkit.Mvvm, Semantic Kernel.
- Solution: `Aemeath.sln` with 4 projects:
  - `src/Aemeath.Core/Aemeath.Core.csproj`
  - `src/Aemeath.Desktop/Aemeath.Desktop.csproj`
  - `src/Aemeath.Pet/Aemeath.Pet.csproj`
  - `src/Aemeath.Speech/Aemeath.Speech.csproj`
- Build defaults: `Directory.Build.props` (`net8.0-windows`, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`).
- Central package versions: `Directory.Packages.props`.
- Scripted build/publish entrypoint: `build.bat`.

## 2) Environment
- OS target: Windows 10/11 x64.
- SDK: .NET 8+ (currently also builds with .NET SDK 10).
- Main app output: `publish/Aemeath.Desktop/Aemeath-agent.exe`.

## 3) Canonical Commands
Run from repo root (`E:\Aemeath`) unless noted.

### Restore
- `dotnet restore Aemeath.sln`

### Build
- Debug build: `dotnet build Aemeath.sln -c Debug`
- Release build: `dotnet build Aemeath.sln -c Release`
- Scripted full build + publish: `build.bat`

### Publish
- `dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop`

### Run
- `publish/Aemeath.Desktop/Aemeath-agent.exe`

### Test
- Current state: no test projects are present in this solution.
- Baseline command (safe): `dotnet test Aemeath.sln`
- List discovered tests: `dotnet test Aemeath.sln --list-tests`

### Single-Test Commands (for future test projects)
- Run one test project: `dotnet test path/to/Project.Tests.csproj`
- Run exact test: `dotnet test --filter "FullyQualifiedName=My.Namespace.Type.TestName"`
- Run by partial name: `dotnet test --filter "Name~TestNamePart"`
- Fast rerun after building: `dotnet test --no-build --filter "FullyQualifiedName~TypeName"`

### Lint / Format
- No dedicated linter config is checked in.
- Optional formatting check: `dotnet format --verify-no-changes`
- Treat build warnings/errors as the main quality gate.

## 4) Repository Layout
- `src/Aemeath.Core`: AI services, settings persistence, MCP integration, tool plugins.
- `src/Aemeath.Desktop`: Avalonia desktop shell, windows, chat/config UI, logging.
- `src/Aemeath.Pet`: pet window behavior, animation/follow services, effects.
- `src/Aemeath.Speech`: speech capture and recognition (Windows native + Whisper).
- `assets/`: shared icons, avatars, GIF assets linked into projects.

## 5) Code Style and Conventions (Observed)
These are inferred from current source and should be matched by default.

### Imports and File Structure
- Keep `using` directives at the top of files.
- File-scoped namespaces are standard: `namespace X.Y.Z;`.
- Alias imports are used where helpful (for example Avalonia image aliases).
- Prefer one primary type per file unless tightly coupled helper types are local.

### Naming
- Public types/members: PascalCase.
- Private fields: `_camelCase`.
- Locals/parameters: camelCase.
- Boolean identifiers usually use `Is/Has/Can` style names.

### Types and Nullability
- Respect nullable reference types (`string?`, `Type?`).
- Avoid null-forgiving (`!`) unless there is no safer alternative.
- `var` is common when RHS type is obvious.
- Prefer explicit, serializable model types for persisted data.

### Async, Threading, and Lifetime
- Use `Task`/`Task<T>` and `IAsyncEnumerable<T>` where streaming is needed.
- Accept and forward `CancellationToken` when practical.
- Use `lock` for shared mutable state and keep lock scope narrow.
- Use `finally` for cleanup (timers, recordings, temp files, disposables).

### Error Handling
- Catch expected failures around file IO, OS APIs, speech/device APIs, and process launching.
- Provide safe fallback behavior/messages when UX should degrade gracefully.
- In desktop flows, log through `AppLogger`.
- Do not add silent catch blocks in new code unless fallback behavior is explicit and intentional.

### Persistence and Paths
- Build paths with `Path.Combine`.
- Prefer `Environment.SpecialFolder` for user/app data locations.
- Persist JSON with `System.Text.Json`; `WriteIndented = true` is commonly used.
- App data convention in this repo is `%AppData%/Aemeath`.

### Avalonia and MVVM Patterns
- Window event wiring commonly happens in constructors.
- Resource URIs for bundled assets use `avares://...`.
- Dispose/stop timers, bitmaps, and services on close/dispose paths.
- Use `Dispatcher.UIThread` for explicit UI thread scheduling when needed.
- CommunityToolkit MVVM attributes are established:
  - `[ObservableProperty]`
  - `[RelayCommand]`
  - `ObservableObject` base class

## 6) Agent Working Rules
- Make minimal, targeted edits; avoid broad rewrites unless required.
- Search for existing module patterns before introducing new approaches.
- Do not edit generated or output artifacts in `bin/`, `obj/`, or `publish/`.
- Keep boundaries clear across Core/Desktop/Pet/Speech projects.
- Preserve surrounding language style for user-facing strings.

## 7) Verification Checklist
After code changes, run the relevant subset (or all for broad changes):
1. `dotnet restore Aemeath.sln` (if project/package inputs changed)
2. `dotnet build Aemeath.sln -c Debug`
3. `dotnet test Aemeath.sln` (expect 0 discovered tests currently)
4. Scenario check for changed area (desktop launch, pet behavior, speech path, etc.)

## 8) Cursor / Copilot Rules Status
Current repository scan found:
- No `.cursor/rules/` directory.
- No `.cursorrules` file.
- No `.github/copilot-instructions.md` file.

If these files are added later, incorporate their instructions into this document and treat them as higher-priority tool-specific constraints.

## 9) Practical Notes for Agents
- Follow existing boundaries:
  - Core: provider/tooling/config logic.
  - Desktop: UI shell and user interaction.
  - Pet: avatar movement/animation behavior.
  - Speech: capture/transcription services.
- Prefer extending existing services over creating parallel abstractions.
- When touching persistence code, keep backward compatibility for existing JSON files.
- When touching UI windows, ensure cleanup in close/dispose paths.
- For async flows that can run long (speech, chat, tool calls), wire cancellation where practical.
- Keep user-facing wording consistent with nearby strings (Chinese text is common in UI messages).

## 10) Change Hygiene
- Verify changed files compile before broader build.
- Keep edits local; avoid unrelated formatting churn.
- Do not commit binaries or generated outputs.
- If adding tests later, mirror project naming conventions (`*.Tests.csproj`) and keep test commands in this document updated.
