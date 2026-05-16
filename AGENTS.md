# AGENTS.md

面向在 `E:\Aemeath` 仓库中工作的自动化编码代理。所有代理都应先阅读本文件，再开始修改代码。

## 1. 项目定位

Aemeath 是一个 Windows 桌面 AI 助手项目，主题围绕“鸣潮 / 爱弥斯 / 小爱”的桌宠式陪伴体验。它不是纯聊天 Demo，而是把桌宠窗口、聊天窗口、Provider/Model 管理、长期记忆、语音输入、唤醒词、本地工具调用和 MCP 能力组合到一个本地桌面应用里。

当前技术栈：

- .NET 8，C#，目标框架由根目录 `Directory.Build.props` 统一为 `net8.0-windows`。
- Avalonia UI 11.2.1，桌面生命周期使用 `IClassicDesktopStyleApplicationLifetime`。
- CommunityToolkit.Mvvm，用于部分 ViewModel 的 observable 属性模式。
- Microsoft Semantic Kernel 1.20.0，用于 LLM Provider 接入和工具插件注册。
- NAudio、Whisper.net、Porcupine，用于录音、转写和唤醒词。
- System.Text.Json，用于设置、会话、记忆等 JSON 持久化。
- Windows DPAPI，通过 `System.Security.Cryptography.ProtectedData` 保护本地 API Key。

解决方案 `Aemeath.sln` 当前包含 4 个项目：

- `src/Aemeath.Core/Aemeath.Core.csproj`
- `src/Aemeath.Desktop/Aemeath.Desktop.csproj`
- `src/Aemeath.Pet/Aemeath.Pet.csproj`
- `src/Aemeath.Speech/Aemeath.Speech.csproj`

主程序发布输出：

- `publish/Aemeath.Desktop/Aemeath-agent.exe`

## 2. 仓库边界和职责

保持项目边界清晰是本仓库最重要的维护原则之一。

### `src/Aemeath.Core`

负责不依赖桌面窗口的核心业务能力。

主要职责：

- AI 聊天服务和 Provider 初始化。
- Provider、API Key、Endpoint、模型列表和默认模型的设置持久化。
- OpenAI-compatible Semantic Kernel 连接器封装。
- 本地知识库读取、搜索和工具插件注册。
- MCP 插件与 MCP 依赖检查、下载、解析。
- 文件系统、浏览器、截图、提醒等工具插件。
- 工具调用确认流程的数据结构和服务。

常见入口：

- `AI/AemiChatService.cs`：聊天服务主入口，负责从设置加载模型、注册工具、发送消息、总结长期记忆。
- `AI/OpenAIKernelMixin.cs`：OpenAI-compatible Provider 的 Kernel 封装。
- `Configuration/SettingsService.cs`：设置读写、API Key 加密解密、Provider/Model 切换。
- `Configuration/ProviderProbeService.cs`：访问 `/models` 获取模型列表并解析。
- `MCP/McpDependencyService.cs`：检查和下载 `uv.exe`、`bun.exe` 等 MCP 依赖。
- `Tools/*Plugin.cs`：本地工具能力。

约束：

- Core 不应引用 Desktop/Pet/Speech。
- Core 不应直接操作 Avalonia 控件。
- Core 中出现用户可见错误时，应返回可展示的中文消息，Desktop 决定如何显示。

### `src/Aemeath.Desktop`

负责 Avalonia 桌面壳、窗口、配置界面、聊天界面、日志和应用生命周期。

主要职责：

- 应用启动与系统托盘菜单。
- 桌宠窗口、聊天窗口、设置窗口之间的打开和关闭协调。
- Provider/Model 快速切换 UI。
- 聊天会话持久化。
- 长期记忆管理界面。
- 用户头像、聊天背景、桌宠设置、语音设置、MCP 设置界面。
- 日志记录。

常见入口：

- `Program.cs`：应用入口，初始化 `AppLogger`，注册全局异常日志。
- `App.axaml` / `App.axaml.cs`：Avalonia 应用、托盘图标、桌宠主窗口、聊天/设置窗口生命周期。
- `Views/ChatWindow.axaml` / `.cs`：聊天主窗口、消息发送、会话切换、Provider/Model 快速切换、语音按钮。
- `Views/ConfigWindow.axaml` / `.cs`：设置中心、Provider 管理、模型列表、记忆管理、桌宠和语音配置。
- `Views/McpConfigWindow.axaml` / `.cs`：MCP 配置窗口。
- `Services/AppLogger.cs`：日志写入。
- `Services/ChatSessionStore.cs`：聊天会话 JSON 存储。
- `Services/LongTermMemoryStore.cs`：长期记忆 JSON 存储。

约束：

- 窗口事件通常在构造函数中绑定，关闭时必须停止计时器和释放资源。
- UI 线程调度使用 `Dispatcher.UIThread`。
- Desktop 可以引用 Core、Pet、Speech，但不要把桌面控件传入 Core。
- 修改窗口关闭逻辑时，必须同时考虑普通关闭、隐藏到托盘、托盘退出、桌宠右键菜单和应用生命周期。

### `src/Aemeath.Pet`

负责桌宠窗口、动画、跟随、吸附和桌宠上下文菜单。

主要职责：

- 显示桌宠 GIF 动画。
- 桌宠拖动、点击、双击打开聊天。
- 鼠标跟随、屏幕边缘吸附。
- 气泡台词和闲置问候。
- 桌宠右键菜单。

常见入口：

- `PetWindow.axaml` / `.cs`：桌宠窗口主逻辑。
- `PetViewModel.cs`：桌宠状态。
- `Services/GifAnimationService.cs`：GIF 帧加载和播放。
- `Services/FollowService.cs`：桌宠跟随鼠标。
- `Effects/ParticleEffect.cs`：粒子效果。

约束：

- 桌宠动画、跟随、气泡和闲置问候都依赖计时器，关闭窗口时必须停止。
- 桌宠尺寸和透明度要尊重 `Settings` 中的范围限制。
- 资源 URI 使用 `avares://Aemeath.Pet/...`。

### `src/Aemeath.Speech`

负责语音捕获、转写和唤醒词。

主要职责：

- 麦克风录音。
- Windows/Whisper 语音识别路径。
- Whisper base 模型下载与缓存。
- Porcupine 唤醒词监听。

常见入口：

- `SpeechService.cs`：语音输入、录音停止后转写、Whisper 模型缓存。
- `MicrophoneHandler.cs`：麦克风音频采集。
- `WakeWordService.cs`：Porcupine 唤醒词服务。

约束：

- 语音和唤醒词都可能持有设备资源，异常和关闭路径必须释放。
- 长耗时操作应支持 `CancellationToken`，至少不要阻塞 UI 线程。
- Whisper 模型缓存路径遵循 `%AppData%\Aemeath\whisper`。

## 3. 根目录文件

- `Aemeath.sln`：解决方案文件。
- `Directory.Build.props`：统一目标框架、Nullable、ImplicitUsings、LangVersion、版本信息。
- `Directory.Packages.props`：集中包版本管理。
- `build.bat`：恢复、Release 构建、自包含发布到 `publish/Aemeath.Desktop`。
- `README.md`：面向用户的项目说明，注意当前文件中可能存在历史编码问题，修改时要特别验证 UTF-8。
- `AGENTS.md`：当前文件，面向自动化代理。
- `.gitignore`：排除构建产物、本地配置、日志、密钥和发布产物。
- `assets/`：共享图标、头像、GIF、语音唤醒资源。
- `tools/installer.iss`：安装包脚本。

不要编辑或提交：

- `bin/`
- `obj/`
- `publish/`
- `installer_output/`
- `TestResults/`
- 本地日志、缓存、dump、临时文件
- 真实 API Key、Token、私密配置、用户记忆、用户聊天记录

## 4. 标准命令

所有命令默认从仓库根目录 `E:\Aemeath` 运行。

### 恢复依赖

```powershell
dotnet restore Aemeath.sln
```

### Debug 构建

```powershell
dotnet build Aemeath.sln -c Debug
```

### Release 构建

```powershell
dotnet build Aemeath.sln -c Release
```

### 完整构建和发布

```bat
build.bat
```

无交互环境可使用：

```bat
build.bat --no-pause
```

### 手动发布桌面应用

```powershell
dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop
```

### 运行发布产物

```powershell
publish/Aemeath.Desktop/Aemeath-agent.exe
```

### 测试

当前解决方案没有测试项目。仍可运行基线命令：

```powershell
dotnet test Aemeath.sln
```

列出测试：

```powershell
dotnet test Aemeath.sln --list-tests
```

未来如果新增测试项目，优先使用：

```powershell
dotnet test path\to\Project.Tests.csproj
dotnet test --filter "FullyQualifiedName=My.Namespace.Type.TestName"
dotnet test --filter "Name~TestNamePart"
dotnet test --no-build --filter "FullyQualifiedName~TypeName"
```

### 格式化

当前没有专用 linter 配置。主要质量门禁是构建警告和错误。

可选格式检查：

```powershell
dotnet format --verify-no-changes
```

不要在无关文件上运行会大面积重写格式的命令。

## 5. 运行时数据和隐私

本项目把用户数据保存在本机。默认目录遵循 `%AppData%\Aemeath`。

典型数据：

- `%AppData%\Aemeath\settings.json`：应用设置、Provider、Endpoint、模型偏好，API Key 会通过 DPAPI 加密后保存。
- `%AppData%\Aemeath\long_term_memory.json`：长期记忆。
- `%AppData%\Aemeath\tools\bin`：MCP 依赖，如 `uv.exe`、`uvx.exe`、`bun.exe`。
- `%AppData%\Aemeath\whisper`：Whisper 模型缓存。
- 应用目录下 `log\yyyyMMdd.log`，如果失败则回退到 `%AppData%\Aemeath\log`。

隐私规则：

- 不要把用户真实 API Key、聊天记录、长期记忆、MCP 私有配置、日志或本地模型缓存提交到仓库。
- 不要在调试输出、文档示例或错误消息中打印完整 API Key。
- 需要展示 Key 时只能使用脱敏形式。
- 修改设置结构时必须考虑旧 JSON 兼容；除非用户明确要求，不做破坏性迁移。

## 6. Provider 和模型配置流

Provider/Model 是本项目的高频修改区域，必须保持行为稳定。

主要模型：

- `Settings.CurrentProvider`：当前 Provider。
- `Settings.DefaultModel`：当前默认模型。
- `Settings.ApiKeys`：按规范化 Provider 名称保存的配置字典。
- `ApiKey.Key`：API Key，保存时加密，读取时解密。
- `ApiKey.Endpoint`：OpenAI-compatible endpoint。
- `ApiKey.ModelId`：该 Provider 当前模型。
- `ApiKey.Models`：该 Provider 已知模型列表。

关键服务：

- `SettingsService.NormalizeProviderName`：Provider 名称规范化，当前逻辑会 trim 并转小写，空值回退 `openai`。
- `SettingsService.UpdateApiKey`：保存 Key、Endpoint、默认模型。
- `SettingsService.SaveProviderModels`：保存模型列表并维护当前默认模型。
- `SettingsService.SwitchCurrentProvider`：切换当前 Provider，并同步默认模型。
- `SettingsService.SwitchCurrentModel`：切换当前模型，并同步当前 Provider。
- `ProviderProbeService.FetchModelsAsync`：请求 `{endpoint}/models`，解析 OpenAI-compatible 模型列表。
- `AemiChatService.TryReloadFromSettings`：根据当前设置重建 Kernel。

修改要求：

- 不要绕过 `SettingsService` 直接写设置文件。
- 切换 Provider/Model 时必须保存设置，并尝试重载聊天服务。
- 切换成功但服务不可用时，UI 应说明配置已切换，但需要检查 API Key、Endpoint 或模型。
- Provider 的显示名称、存储键和比较逻辑要统一使用规范化名称，避免大小写产生重复 Provider。
- 模型列表为空时要保留当前模型作为候选，避免用户失去已配置的模型。

## 7. 聊天窗口注意事项

`ChatWindow` 负责聊天主体验，也集成了快速切换、会话管理、语音按钮和工具确认卡。

重点状态：

- `_isSending`：正在生成回复。
- `_isLoadingProviderSwitch`：Provider/Model ComboBox 正在程序化刷新。
- `_providerSwitchLock`：防止重复切换。
- `_pendingToolActions`：等待用户确认的工具操作。
- `_voiceHolding`：长按录音中。

Provider/Model 快速切换规则：

- 发送中、录音中、有待确认工具操作时，不允许切换。
- 刷新 ComboBox 前应清空 `SelectedItem` 和 `SelectedIndex`，再清空 `Items`，避免 Avalonia 保留旧索引造成越界。
- 程序化刷新期间必须设置 `_isLoadingProviderSwitch = true`，让 `SelectionChanged` 事件短路。
- 切换后调用 `TryReloadChatServiceFromSettings`。
- UI 错误提示应区分“切换失败”和“切换成功但服务不可用”。

会话规则：

- 会话由 `ChatSessionStore` 管理。
- 当前会话为空时，发送消息前调用 `EnsureCurrentSession`。
- 删除会话时同步清理当前会话记忆。
- 渲染历史消息时继续清理模型的思考标签和工具噪声。

工具确认规则：

- 文件删除、覆盖、清空等高风险操作必须走 `ToolConfirmationService`。
- 不要绕过确认卡直接执行危险操作。
- 有 pending tool action 时，避免切换 Provider/Model 造成上下文状态错乱。

## 8. 应用生命周期和系统托盘

应用生命周期集中在 `App.axaml.cs`。

启动流程：

- `Program.Main` 初始化日志并启动 Avalonia。
- `App.OnFrameworkInitializationCompleted` 创建 `SettingsService`、`AemiChatService`、`PetWindow` 和 `WakeWordService`。
- `desktop.MainWindow` 当前是桌宠窗口。
- 系统托盘菜单定义在 `App.axaml`。

窗口关系：

- 桌宠窗口负责打开聊天窗口和设置窗口。
- 聊天窗口关闭时应释放计时器、粒子、语音对象和头像位图。
- 设置窗口关闭时应停止粒子和闪烁计时器。
- 桌宠窗口关闭时应释放动画服务并停止跟随、气泡、闲置问候计时器。

托盘行为：

- 如果 `Settings.MinimizeToTray == true`，普通关闭桌宠窗口应取消关闭并隐藏。
- 托盘菜单“退出 Aemeath”必须是真正退出：停止唤醒词、关闭聊天窗口、关闭设置窗口、关闭桌宠窗口，然后调用桌面生命周期 `Shutdown`。
- 真正退出时应使用应用级退出标记，避免 `OnPetWindowClosing` 再次把关闭改成隐藏。

修改生命周期时要手动验证：

- 桌宠可隐藏到托盘。
- 托盘可重新唤出桌宠。
- 托盘退出后进程结束。
- 聊天窗口或设置窗口打开时，托盘退出也能结束进程。

## 9. MCP 依赖和工具

MCP 能力分两部分：

- Core 中的 MCP 插件和依赖管理。
- Desktop 设置窗口中的 MCP 配置和依赖下载按钮。

关键文件：

- `src/Aemeath.Core/MCP/McpChatPlugin.cs`
- `src/Aemeath.Core/MCP/McpDependencyService.cs`
- `src/Aemeath.Desktop/Views/McpConfigWindow.axaml.cs`
- `src/Aemeath.Desktop/Views/ConfigWindow.axaml.cs`

依赖策略：

- 仓库不提交大型运行时二进制。
- 如果根目录 `bin/` 中存在 `uv.exe`、`uvx.exe`、`bun.exe`，项目可在构建/发布时按条件复制。
- 推荐运行时下载到 `%AppData%\Aemeath\tools\bin`。
- 下载逻辑会尝试多个国内镜像，并避免被镜像重定向回 GitHub 官方下载域。

修改要求：

- 下载、解压、校验路径必须使用 `Path.Combine` 和安全的标准库 API。
- 不要硬编码用户目录。
- 下载失败要返回明确中文错误，不要静默吞掉。
- 不要把下载的 exe 或压缩包提交到仓库。

## 10. 语音和唤醒词

语音能力涉及设备、权限、模型下载和后台监听，修改时要保守。

关键路径：

- `SpeechService.StartCaptureAsync`
- `SpeechService.StopCaptureAndRecognizeAsync`
- `SpeechService.EnsureBaseModelAsync`
- `WakeWordService.Start`
- `WakeWordService.Stop`
- `WakeWordService.Dispose`
- `ChatWindow.HandleWakeWordAsync`
- `App.RestartWakeWordServiceAsync`

规则：

- 唤醒词服务启动前必须检查设置是否启用，以及 Picovoice AccessKey 是否存在。
- 唤醒词触发后应停止监听、打开聊天窗口、执行语音捕获，再恢复监听。
- 设备和模型相关异常必须记录日志并给出可恢复路径。
- `SpeechService` 和 `WakeWordService` 持有本机资源，必须在关闭和异常路径释放。
- 长按录音太短时应忽略，避免误触。

## 11. Avalonia 和 UI 风格

本项目 UI 已有明确的暗色科幻风格。修改 UI 时应贴合现有视觉，而不是引入全新设计语言。

通用规则：

- 优先使用现有控件风格和资源。
- 控件事件通常在构造函数中绑定。
- 不要在紧凑工具栏中放过长文案。
- 用户可见字符串以中文为主，保持与附近文案风格一致。
- 资源 URI 使用 `avares://...`。
- 必要时使用 `Dispatcher.UIThread.Post` 或 `Dispatcher.UIThread.InvokeAsync` 回到 UI 线程。
- UI 计时器使用 `DispatcherTimer`，关闭窗口时停止。
- 位图、语音服务、动画服务等可释放对象必须释放。

ComboBox 特别规则：

- 清空 `Items` 前先清空选中态。
- 程序化刷新期间用布尔标志抑制 `SelectionChanged`。
- `SelectedItem` 和 `SelectedIndex` 可能触发事件，不能假设它们只是简单赋值。

## 12. 代码风格

保持与现有代码一致。

文件结构：

- `using` 放在文件顶部。
- 使用文件作用域 namespace：`namespace Aemeath.X;`。
- 通常一个主要类型一个文件。
- 小型 DTO、record 或紧耦合 helper 可放在同文件底部。

命名：

- public 类型、属性、方法：PascalCase。
- private 字段：`_camelCase`。
- 局部变量和参数：camelCase。
- bool 命名优先使用 `Is`、`Has`、`Can`、`Enable` 等语义。

类型和 nullability：

- 尊重 Nullable Reference Types。
- 避免随意使用 null-forgiving `!`。
- `var` 可在右侧类型明显时使用。
- 持久化数据优先定义明确模型，不用松散字典承载核心配置。

异步和线程：

- I/O、网络、语音、模型调用使用 `Task` / `Task<T>`。
- 流式输出使用 `IAsyncEnumerable<T>`。
- 有取消入口时传递 `CancellationToken`。
- 对共享可变状态使用 `lock` 或 `SemaphoreSlim`，锁范围保持短小。
- 资源清理放在 `finally` 或关闭/释放路径中。

异常处理：

- 文件 I/O、OS API、设备 API、网络请求、进程启动、模型服务调用都应捕获预期异常。
- 不要新增无解释的空 `catch`。如果必须降级，降级行为要清晰。
- Desktop 层应通过 `AppLogger` 记录异常。
- 用户可见错误尽量给出下一步检查方向。

持久化：

- 路径使用 `Path.Combine`。
- 用户数据目录优先使用 `Environment.SpecialFolder.ApplicationData`。
- JSON 使用 `System.Text.Json`。
- 需要可读配置时保留 `WriteIndented = true`。
- 修改设置模型时保持旧文件可读。

## 13. 中文和编码

本仓库包含大量中文 UI、文档和资源名。编辑时必须小心编码。

规则：

- 新写或重写中文文件时使用 UTF-8。
- 不要把已有中文字符串改成乱码。
- 如果终端显示乱码，不代表文件一定是乱码；修改前优先用编辑器或 UTF-8 读取方式确认。
- 对 README、AGENTS、UI 字符串做大改后，要抽样查看实际文件内容。
- 资源文件名可能包含中文，移动或重命名前确认项目文件中的 `AvaloniaResource` / `None` 链接同步更新。

## 14. 安全和工具调用

本项目的工具插件可能操作本机文件和进程。安全规则优先于便利性。

文件工具：

- 删除、覆盖、清空、批量移动等高风险操作必须有确认流程。
- 路径要规范化，避免越权访问或误删。
- 不要默认操作仓库外路径，除非用户明确要求。

浏览器/应用工具：

- 打开本机应用或网页时应提供合理 fallback。
- 执行进程命令时限制参数和危险命令。
- 高风险命令应拦截或确认。

截图工具：

- 截图属于本地隐私数据，使用结果时避免泄露无关内容。

日志：

- 日志用于调试，不用于保存敏感正文。
- 不要记录完整 API Key。
- 不要记录用户大量聊天内容，除非已有逻辑明确需要且经过脱敏或用户允许。

## 15. 测试和验证习惯

根据改动范围选择验证强度。

小型 Core 改动：

- `dotnet build Aemeath.sln -c Debug`
- 如涉及设置读写，手动检查旧配置兼容。

Desktop UI 改动：

- `dotnet build Aemeath.sln -c Debug`
- 手动启动应用，验证相关窗口。
- 重点检查关闭、隐藏、重新打开、托盘退出、计时器释放。

Provider/Model 改动：

- 验证设置窗口保存 Provider。
- 验证聊天窗口快速切换 Provider。
- 验证聊天窗口快速切换 Model。
- 验证切换后关闭聊天窗口再打开仍保持选择。
- 验证 API Key 或 Endpoint 不可用时提示合理。

Pet 改动：

- 验证拖动、双击打开聊天、右键菜单、跟随、边缘吸附、气泡、关闭释放。

Speech 改动：

- 验证无麦克风权限、无 AccessKey、无模型文件、模型下载失败等降级路径。
- 验证录音服务停止后资源释放。

MCP 改动：

- 验证依赖检查。
- 验证缺少 `uv.exe` / `bun.exe` 时下载路径。
- 验证已有依赖时不重复下载。

提交前建议：

```powershell
dotnet build Aemeath.sln -c Debug
dotnet test Aemeath.sln
```

当前没有测试项目，因此 `dotnet test Aemeath.sln` 主要作为未来测试接入后的基线命令。

## 16. 变更卫生

修改原则：

- 做最小、针对性的改动。
- 先搜索已有模式，再引入新实现。
- 不为局部问题做全局重构。
- 不做无关格式化。
- 不改生成产物。
- 不提交二进制发布产物。
- 不覆盖用户未要求修改的本地变更。
- 如果同一文件已有用户改动，先理解再合并自己的改动。

新增功能原则：

- 优先扩展现有服务，不创建平行抽象。
- 保持 Core/Desktop/Pet/Speech 边界。
- UI 和业务逻辑分层清楚。
- 持久化格式要向后兼容。
- 长耗时任务要考虑取消、异常和资源释放。

新增测试原则：

- 如果未来添加测试项目，命名建议为 `Aemeath.Core.Tests`、`Aemeath.Desktop.Tests` 等。
- 测试项目路径建议放在 `tests/` 或对应 `src` 旁的清晰目录中，并同步更新本文件命令。
- Provider/Model、设置迁移、工具确认、安全拦截、MCP 解析等逻辑最适合优先补单元测试。

## 17. 当前仓库规则扫描

当前仓库未发现以下工具专用规则文件：

- `.cursor/rules/`
- `.cursorrules`
- `.github/copilot-instructions.md`

如果后续新增这些文件，应把其中与本项目相关且更具体的规则合并到本文件，并按工具要求处理优先级。

## 18. 代理执行清单

开始任务前：

- 确认工作树状态。
- 阅读相关文件，不凭印象修改。
- 明确改动属于 Core、Desktop、Pet、Speech 或文档。
- 检查是否会触碰用户数据、密钥、发布产物或生成目录。

修改代码时：

- 保持现有风格。
- 保持中文 UI 文案一致。
- 对可失败路径记录日志或给出明确 fallback。
- 对窗口、计时器、设备、位图、服务补齐释放路径。

完成后：

- 运行相关构建或测试。
- 汇报改动文件和验证结果。
- 如果未能运行某项验证，说明原因。
- 不隐藏残留风险，尤其是需要真实 UI 或设备手动验证的部分。
