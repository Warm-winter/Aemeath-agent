# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

Aemeath 是一个面向鸣潮玩家的「爱弥斯」主题 Windows AI 桌宠助手（.NET 8 + Avalonia UI）。角色名为「小爱」。核心形态是一个桌面宠物窗口，双击打开聊天窗口，并集成 Semantic Kernel 工具、本地知识库、长期记忆、MCP 插件和语音输入。

**永远用中文与用户交流**（用户全局指令）。

## 常用命令

```powershell
# 还原 + 调试构建（提交前建议先跑通）
dotnet restore Aemeath.sln
dotnet build Aemeath.sln -c Debug

# 发布自包含版本（win-x64），输出到 publish/Aemeath.Desktop/
dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop

# 一键构建+发布（Windows bat 脚本，内部执行 restore → build Release → publish）
build.bat
build.bat --no-pause   # CI 环境用
```

- **没有测试项目**。README 中提到的 `dotnet test Aemeath.sln` 只是预留基线命令，当前不会运行任何测试。验证改动靠 `dotnet build` + 运行应用。
- 启动入口是 `src/Aemeath.Desktop/Program.cs`，输出程序集名 `Aemeath-agent`（不是项目名）。发布后可执行文件为 `publish/Aemeath.Desktop/Aemeath-agent.exe`。
- 构建需要 .NET SDK 8.0+，Windows 10/11 x64。Whisper/Speech 依赖带 RID 图（`UseRidGraph=true`）。

## 解决方案结构（4 个项目）

```
Aemeath.Core    —— AI、设置、知识库、MCP、Semantic Kernel 工具插件（无 UI 依赖）
Aemeath.Desktop —— Avalonia 主程序：聊天窗口、配置窗口、托盘、长期记忆、日志
Aemeath.Pet     —— 桌宠窗口、GIF 动画、鼠标跟随、边缘吸附、交互
Aemeath.Speech  —— 录音 + Whisper.net 语音识别
```

引用方向：`Desktop → {Core, Pet, Speech}`，`Pet → Core`，`Speech` 独立。UI 代码主要在 Desktop 和 Pet，Core 应保持无 Avalonia 依赖。

包版本集中在根目录 `Directory.Packages.props`（ centrally managed）。改版本改这里，不要在单个 csproj 里写 `Version`。全局编译属性在 `Directory.Build.props`（`net8.0-windows`、`Nullable=enable`、`ImplicitUsings=enable`）。

## 运行时数据与隐私约定（重要）

所有用户数据落在 `%AppData%\Aemeath\`：

| 文件 | 内容 | 由谁管理 |
|------|------|---------|
| `settings.json` | 全部设置 + 各 Provider 的 API Key（DPAPI 加密） | `SettingsService` |
| `long_term_memory.json` | 长期记忆 | `LongTermMemoryStore` |
| `mcp_servers.json` / `mcp_memory*.json` | MCP 服务配置与记忆 | `McpServerStore` |
| `tools/bin/uv.exe`, `bun.exe` | 运行时下载的 MCP 依赖 | `McpDependencyService` |

**API Key 用 Windows DPAPI（`ProtectedData`，CurrentUser 范围）加密后再写盘**，读出来时解密。`SettingsService.Save()` 会重加密所有 key。这些文件名都在 `.gitignore` 中，禁止提交真实 key / 记忆 / 发布产物。

日志写到 `<AppContext.BaseDirectory>/log/yyyyMMdd.log`（`AppLogger`，全静态、带锁、静默吞异常）。

## 核心架构

### 应用启动与窗口编排（`App.axaml.cs`）

`App` 是单例编排中心，构造 `SettingsService` → `AemiChatService`，并持有 Pet/Chat/Config 三个窗口的引用，懒加载、可重入地 Show/Activate。

- **主窗口是 `PetWindow`**（桌宠），不是聊天窗口。聊天/配置通过桌宠的双击/右键菜单触发。
- **单实例**：`Program.cs` 用命名 Mutex `Local\Aemeath.Desktop.SingleInstance` 防止多开。
- **聊天活动 → 桌宠状态联动**：`ChatWindow` 通过 `ActivityChanged` 事件把 Sending/VoiceListening/ToolWaiting/Completed/Failed 等状态传给 `App`，`App` 再调用 `PetWindow.SetActivityState` / `PlayTemporaryStateAsync` 让桌宠播放对应动画（执行任务=Running、聆听=Waiting、任务完成=Review、失败=Failed）。改聊天流程时注意这条联动链。
- MCP 工具在启动后**后台异步加载**（`StartMcpBackgroundReload`），不阻塞 UI；状态经 `McpStatusChanged` 事件上报。

### AI 抽象（`Aemeath.Core/AI/`）

- `IChatService` 是接口，`AemiChatService` 是唯一实现，聚合了 settings、知识库、工具确认、MCP 运行时。
- `KernelMixinBase`（抽象）封装 Semantic Kernel。可用实现只有 `OpenAIKernelMixin`——所有 Provider 走 **OpenAI-compatible** 协议（`AddOpenAIChatCompletion`，可选自定义 endpoint）。仓库里的 `AnthropicKernelMixin` 只是占位，构造可用但 `InitializeAsync`/`BuildKernel` 抛 `NotSupportedException`，引导用户走 OpenAI 兼容协议；不要把它当成可用的 Provider 实现。
- **工具自动调用**：用 `ToolCallBehavior.AutoInvokeKernelFunctions`，注册的 KernelFunction 会被模型直接调用。相关代码用 `#pragma warning disable SKEXP0001/SKEXP0010` 关掉实验性 API 警告——这是有意的，不要去掉这些 pragma。
- **附件处理**：`KernelMixinBase.BuildUserContentItemsAsync` 把文本附件内联（截断到 12 万字符）、图片转 `ImageContent`、其它文件只附路径。聊天页负责收集 `ChatAttachment`。
- **知识库注入**：每条用户消息发送前，`EnrichMessageWithKnowledge` 会在前面拼上本地知识库命中片段和检索规则。系统提示词在 `Prompts/AemiSystemPrompt.cs`（Default / Professional 两套）。
- **响应清洗**：`FormatAemiResponse` 会剥离 `<think>` 块、`/think`、` ```think ` 等推理模型残留。MCP 等待标记也会在 `ChatWindow` 中被正则清掉（见下）。

### 工具确认机制（高风险操作，跨 Core/Desktop）

这是项目里最需要注意的跨层约定：

1. 工具插件（`FileSystemPlugin`、`BrowserPlugin`）遇到删除/覆盖/清空类操作时，**不直接执行**，而是调用 `ToolConfirmationService.RequestConfirmation(title, desc, execute)`。
2. 该方法把待执行动作存入内存，返回一个标记字符串 `AEMEATH_PENDING_CONFIRMATION:<guid>`，这个字符串会作为「工具结果」回到模型回复里。
3. `ChatWindow` 订阅 `PendingActionCreated` 事件，弹出确认卡片；同时用正则把 `PendingMarkerPrefix` 从显示文本里抹掉（不让用户看到内部标记）。
4. 用户点确认 → `Confirm(id)` → 执行原始闭包；点取消 → `Cancel(id)`。

**改任何工具插件或聊天渲染逻辑时，都要保证这条标记链不断**：标记必须能流回 UI、UI 必须能识别并渲染卡片、确认后必须能找到并执行闭包。

### 桌宠动画状态机（`Aemeath.Pet/`）

`PetWindow` 维护一个分层状态优先级：**临时状态 > 活动状态(activity) > 跟随/待机基础状态**。

- GIF 资源在 `assets/animations/pet/*.gif`，通过 `avares://Aemeath.Pet/Assets/animations/pet/...` 加载，状态映射在 `LoadGifAssetsAsync`。
- `PetState` 枚举：Idle / Follow / FollowLeft / Click / Wave / Jump / Failed / Waiting / Running / Review 等。
- 跟随鼠标用 `FollowService` + 20ms `DispatcherTimer`；单击/双击用 350~360ms 延迟去抖判定（双击打开聊天，单击播放 Click 动画）。
- 拖拽用 Win32 `GetCursorPos` 取全局光标坐标（Avalonia 坐标系不够用），并 `ClampToScreen` / `SnapToEdgeIfNeeded` / `DockToNearestEdge` 做边缘吸附。
- 右键菜单（`OnContextRequested`）大量直接修改 `SettingsService.Current` 并 `Save()`——桌宠是改设置的一条主路径。

### 长期记忆（`LongTermMemoryStore`）

JSON 存储，分 `global` 和 `session` 两个 scope。`SaveSummary` 由 `ChatSessionStore` 在对话进行到一定轮次后触发（用 `IChatService.SummarizeAsync` 让模型压缩），把 summary/fact/task/preference 写入。`BuildPromptBlock` 在发送时拼回系统上下文。

### MCP（`Aemeath.Core/MCP/`）

- `McpRuntimeService` 支持 stdio / SSE / HTTP 三种 transport，通过 `ModelContextProtocol` 客户端连接。
- 工具被包装成名为 `mcp_<server>_<tool>` 的 KernelFunction，统一接收一个 `argumentsJson` 字符串参数（避免强类型签名问题）。函数名有去重和大小写规范化（`NormalizeFunctionName`）。
- **加载是分超时档位的**：后台加载（stdio 30s / http 150s）与手动测试（stdio 60s / http 180s）不同，见 `GetTimeout`。stdio 失败会捕获 stderr 摘要拼进错误信息。
- uv.exe / bun.exe 体积大不进 Git，首次使用由 `McpDependencyService` 从国内镜像下载到 `%AppData%\Aemeath\tools\bin`。`Aemeath.Desktop.csproj` 用 `Condition="Exists(...)"` 条件引用 `bin\*.exe`——本地没有也能构建。

## UI 约定

- 颜色/组件令牌集中在 `Aemeath.Desktop/Services/AemiUi.cs`（静态助手），主样式在 `Styles/AemeathTheme.axaml`。新增 UI 尽量复用这些令牌，保持「爱弥斯粉」视觉一致性。
- **响应必须是纯文本，禁止 Markdown**（见系统提示词「回复格式限制」）。聊天渲染层也按纯文本处理。
- 角色口吻要点：自称「小爱」（第三人称），称呼用户「漂泊者」，不主动暴露工具编号/函数名/命令/.exe 名等内部技术痕迹。
- `Behaviors/ImeFixBehavior.cs` 是中文输入法光标修复，`ChatWindow` 体量很大（2300+ 行），改动前先定位相关方法。

## 提交与文件注意

- `.gitignore` 已排除 `bin/ obj/ publish/ /bin/ *.exe`（但放行 `tools/**/*.iss`）、`settings.json`、`long_term_memory.json`、`mcp_servers.json`、`*.log` 等。**不要把真实 API Key、用户记忆、发布产物、大体积二进制提交进仓库。**
- Inno Setup 脚本在 `tools/installer.iss`（可选，做安装包用）。
- 最近提交信息多为中文，描述修复内容，风格可保持一致。
