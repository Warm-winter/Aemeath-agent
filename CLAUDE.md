# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

Aemeath 是一个面向鸣潮玩家的「爱弥斯」主题 Windows AI 桌宠助手（.NET 8 + Avalonia UI）。角色名为「小爱」。核心形态是一个桌面宠物窗口，双击打开聊天窗口，并集成 Semantic Kernel 工具、本地知识库、Skill 人格框架、长期记忆、MCP 插件和语音输入。

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
Aemeath.Core    —— AI、设置、知识库、Skill、MCP、Semantic Kernel 工具插件（无 UI 依赖）
Aemeath.Desktop —— Avalonia 主程序：聊天窗口、配置窗口、托盘、长期记忆、日志
Aemeath.Pet     —— 桌宠窗口、GIF 动画、鼠标跟随、边缘吸附、交互
Aemeath.Speech  —— 录音 + Whisper.net 语音识别
```

引用方向：`Desktop → {Core, Pet, Speech}`，`Pet → Core`，`Speech` 独立。UI 代码主要在 Desktop 和 Pet，**Core 必须保持无 Avalonia 依赖**。

包版本集中在根目录 `Directory.Packages.props`（centrally managed）。改版本改这里，不要在单个 csproj 里写 `Version`。全局编译属性在 `Directory.Build.props`（`net8.0-windows`、`Nullable=enable`、`ImplicitUsings=enable`）。

## 运行时数据与隐私约定（重要）

所有用户数据落在 `%AppData%\Aemeath\`：

| 路径 | 内容 | 由谁管理 |
|------|------|---------|
| `settings.json` | 全部设置 + 各 Provider 的 API Key（DPAPI 加密）+ Azure 语音 key | `SettingsService` |
| `long_term_memory.json` | 长期记忆（global / session 两种 scope） | `LongTermMemoryStore` |
| `chat_sessions.json` | 多会话记录 | `ChatSessionStore` |
| `mcp/servers/*.json` | 每个 MCP 服务一个 JSON 文件（已从单文件 `mcp_servers.json` 迁移） | `McpServerStore` |
| `skills_state.json` | 被禁用的用户 Skill 名单 | `SkillService` |
| `skills/<name>/` | 用户导入的自定义 Skill 目录 | `SkillService` |
| `mem0/` | Mem0 记忆库（Qdrant 本地 + history.db）+ `bridge/mem0_bridge.py` | `Mem0Client` / `Mem0DependencyService` |
| `tools/mem0-venv/` | Mem0 专用 Python venv | `Mem0DependencyService` |
| `tools/ufo-bridge/ufo_runner.py` | UFO 桥接脚本（内嵌资源释放） | `BridgeAssetDeployer` / `UfoRunner` |
| `tools/bin/uv.exe`, `bun.exe` | 运行时下载的 MCP 依赖 | `McpDependencyService` |

**API Key 用 Windows DPAPI（`ProtectedData`，CurrentUser 范围）加密后再写盘**，读出来时解密。`SettingsService.Save()` 会重加密所有 key；旧版本明文存储时 `TryDecrypt` 解密失败会原样返回，保持向后兼容。这些文件名都在 `.gitignore` 中，禁止提交真实 key / 记忆 / 发布产物。

日志写到 `<AppContext.BaseDirectory>/log/yyyyMMdd.log`（`AppLogger`，全静态、带锁、静默吞异常）。

## 核心架构

### 应用启动与窗口编排（`App.axaml.cs`）

`App` 是单例编排中心，构造 `SettingsService` → `AemiChatService`，并持有 Pet/Chat/Config 三个窗口的引用，懒加载、可重入地 Show/Activate。

- **主窗口是 `PetWindow`**（桌宠），不是聊天窗口。聊天/配置通过桌宠的双击/右键菜单触发。
- **单实例**：`Program.cs` 用命名 Mutex `Local\Aemeath.Desktop.SingleInstance` 防止多开。
- **聊天活动 → 桌宠状态联动**：`ChatWindow` 通过 `ActivityChanged` 事件把 Sending/VoiceListening/ToolWaiting/Completed/Failed 等状态传给 `App`，`App` 再调用 `PetWindow.SetActivityState` / `PlayTemporaryStateAsync` 让桌宠播放对应动画（执行任务=Running、聆听=Waiting、任务完成=Review、失败=Failed）。改聊天流程时注意这条联动链。
- MCP 工具在启动后**后台异步加载**（`StartMcpBackgroundReload`），不阻塞 UI；状态经 `McpStatusChanged` 事件上报。退出时 `ExitApplication` 会先 `DisposeAsync` 释放 MCP 子进程再 `Shutdown`。
- 配置窗口（`ConfigWindow`）内含多个面板，新近加入 MCP 配置面板（`McpConfigPanel`）和 Skill 管理面板（`SkillConfigPanel`），均参考 cherry-studio 设计。

### AI 抽象（`Aemeath.Core/AI/`）

- `IChatService` 是接口，`AemiChatService` 是唯一实现，聚合了 settings、知识库、Skill、工具确认、MCP 运行时。
- `KernelMixinBase`（抽象）封装 Semantic Kernel。可用实现只有 `OpenAIKernelMixin`——所有 Provider 走 **OpenAI-compatible** 协议（`AddOpenAIChatCompletion`，可选自定义 endpoint）。仓库里的 `AnthropicKernelMixin` 只是占位，构造可用但 `InitializeAsync`/`BuildKernel` 抛 `NotSupportedException`，引导用户走 OpenAI 兼容协议；**不要把它当成可用的 Provider 实现**。
- **工具自动调用**：用 `ToolCallBehavior.AutoInvokeKernelFunctions`，注册的 KernelFunction 会被模型直接调用。相关代码用 `#pragma warning disable SKEXP0001/SKEXP0010` 关掉实验性 API 警告——这是有意的，不要去掉这些 pragma。
- **系统提示词 = Skill 人格 + 能力基座**：`InitializeFromSettings` 先 `_skillService.LoadAll()` 拿到人格提示词，再拼上 `AemiSystemPrompt.CapabilityBase`（与角色无关的工具/格式规则）。`Prompts/AemiSystemPrompt.cs` 只有 `CapabilityBase` 和 `FallbackPersona`（skill 全部缺失时的降级人格）两个常量，没有多套预设人格——人格来源完全交给 Skill 框架。
- **附件处理**：`KernelMixinBase.BuildUserContentItemsAsync` 把文本附件内联（截断到 12 万字符）、图片转 `ImageContent`、其它文件只附路径。聊天页负责收集 `ChatAttachment`。
- **知识库 + 工具清单注入**：每条用户消息发送前，`EnrichMessageWithKnowledge` 在前面拼上本地知识库命中片段、检索规则，以及当前已加载的 MCP 工具清单和强制调用规则。
- **响应清洗**：`FormatAemiResponse` 会剥离 `<think>` 块、`/think`、` ```think ` 等推理模型残留。MCP 等待标记也会在 `ChatWindow` 中被正则清掉。

### Skill 框架（`Aemeath.Core/Skills/`）— 人格与知识的统一来源

项目用通用的 Agent Skill 约定来承载角色人格和知识，**不再把人格写死在系统提示词里**：

- 每个 Skill 是一个目录，含 `SKILL.md` 入口（顶部是 `---` 包裹的 YAML frontmatter：`name` / `description` / 「触发词」），同目录其他 `.md` 作为人格/知识素材。
- 内置 Skill `aemeath`（人格 + 鸣潮/爱弥斯知识库）以**内嵌资源**打包（`Aemeath.Core.csproj` 用 `<EmbeddedResource>` + `<LogicalName>` 固定资源名 `Aemeath.Skills.aemeath.*.md`）。文件包括 `profile` / `personality` / `interaction` / `memory` / `relations` / `conflicts`。
- 用户 Skill 从 `%AppData%\Aemeath\skills\<name>\` 加载。
- `SkillLoader.BuildPackage` 把每个文件分类：`SKILL.md` 正文 + `interaction.md` → 人格提示词；其余背景文件（profile/personality/memory/...）→ 转成 `KnowledgeBaseEntry`（标题/别名有约定映射）。
- `SkillService` 聚合所有**已启用** Skill 的人格和知识条目，分别注入系统提示词和知识库。**内置 Skill 恒启用、不可删除/禁用**（加载时强制 `Enabled=true`）；用户 Skill 的启用状态持久化在 `skills_state.json`（记录的是被禁用名单）。
- `AemiChatService.ReloadSkills()` 用于面板变更后重建 Skill + 系统提示词 + 知识库；UI 的 `SkillConfigPanel` 提供启用/禁用/删除/导入目录操作。

### 工具确认机制（高风险操作，跨 Core/Desktop）

这是项目里最需要注意的跨层约定：

1. 工具插件（`FileSystemPlugin`、`BrowserPlugin`）遇到删除/覆盖/清空类操作时，**不直接执行**，而是调用 `ToolConfirmationService.RequestConfirmation(title, desc, execute)`。
2. 该方法把待执行动作存入内存，返回一个标记字符串 `AEMEATH_PENDING_CONFIRMATION:<guid>`，这个字符串会作为「工具结果」回到模型回复里。
3. `ChatWindow` 订阅 `PendingActionCreated` 事件，弹出确认卡片；同时用正则把 `PendingMarkerPrefix` 从显示文本里抹掉（不让用户看到内部标记）。
4. 用户点确认 → `Confirm(id)` → 执行原始闭包；点取消 → `Cancel(id)`。

**改任何工具插件或聊天渲染逻辑时，都要保证这条标记链不断**：标记必须能流回 UI、UI 必须能识别并渲染卡片、确认后必须能找到并执行闭包。

另外 `FileSystemPlugin` 有路径白名单（`IsAllowedRoots`）：只允许用户主目录和系统临时目录，且**明确禁止进入 `%AppData%\Aemeath`**（防止读写 settings/记忆等含凭据文件）。新工具插件访问文件系统时沿用这套约束。

### 桌宠动画状态机（`Aemeath.Pet/`）

`PetWindow` 维护一个分层状态优先级：**临时状态 > 活动状态(activity) > 跟随/待机基础状态**。

- GIF 资源在 `assets/animations/pet/*.gif`，通过 `avares://Aemeath.Pet/Assets/animations/pet/...` 加载，状态映射在 `LoadGifAssetsAsync`。
- `PetState` 枚举：Idle / Follow / FollowLeft / Click / Wave / Jump / Failed / Waiting / Running / Review 等。
- 跟随鼠标用 `FollowService` + 20ms `DispatcherTimer`；单击/双击用 350~360ms 延迟去抖判定（双击打开聊天，单击播放 Click 动画）。
- 拖拽用 Win32 `GetCursorPos` 取全局光标坐标（Avalonia 坐标系不够用），并 `ClampToScreen` / `SnapToEdgeIfNeeded` / `DockToNearestEdge` 做边缘吸附。
- 右键菜单（`OnContextRequested`）大量直接修改 `SettingsService.Current` 并 `Save()`——桌宠是改设置的一条主路径。

### 长期记忆（Mem0）— 跨 Core/Desktop 的 Python 子进程方案

长期记忆改由开源项目 **Mem0** 提供（Apache 2.0，许可证在 `assets/notices/mem0-LICENSE.txt`），替代了旧的 JSON 记忆系统（`LongTermMemoryStore` / `MemorySummarizer` / 内置 MCP `@modelcontextprotocol/server-memory` 全部已删除）。

架构：C# ↔ Python 子进程 JSON-RPC over stdio。
- `Aemeath.Core/Memory/Mem0Client.cs`：长驻 Python 子进程管理 + 行协议请求/响应（ping/health/add/search/get_all/delete/delete_all）。`mem0_bridge.py` 作为内嵌资源（`Aemeath.Core.Memory.mem0_bridge.py`）在首次使用时释放到 `%AppData%\Aemeath\mem0\bridge\`。
- `Aemeath.Core/Memory/MemoryOrchestrator.cs`：业务编排——每轮对话结束后 `AddTurnAsync`（Mem0 内部 LLM 自动抽取事实，无需手动压缩），发送前 `BuildRelevantMemoryBlockAsync` 按当前消息向量检索并拼进提示词。作用域 `Mem0Scope.GlobalUser`（固定 user_id=`drifter`）+ `Mem0Scope.ForSession(sessionId)`。
- `Aemeath.Core/Memory/Mem0DependencyService.cs`：用 uv 创建独立 venv（`%AppData%\Aemeath\tools\mem0-venv`）并装 `mem0ai` + `qdrant-client`，不污染系统 Python。
- **Provider 配置注入**：`AemiChatService.BuildMem0Config()` 把当前 Provider 的 base_url/api_key/模型传给 Mem0 的 `llm` + `embedder`（字段名 `openai_base_url`）。切 Provider 时 orchestrator 会重建 client。
- **依赖未装时静默降级**：`MarkUnavailable` 短路，不影响聊天；设置面板「记忆管理 → 安装 Mem0 依赖」一键安装。
- 桌面层：`ChatWindow` 持有 `MemoryOrchestrator`（每轮 add + 发送前 search），`ConfigWindow` 的记忆管理面板从 Mem0 读取/编辑/删除/清空（每个操作临时建 `Mem0Client`）。
- 记忆数据落在 `%AppData%\Aemeath\mem0\`（Qdrant 本地库 + history.db）。

### 多模态：图片识别（VisionPlugin）

`Aemeath.Core/Tools/VisionPlugin.cs`：让纯文本模型也能「看」图片。移植自 NousResearch/hermes-agent（MIT）的 `vision_analyze` 工具——接收 `image_source`（本地路径，走与 FileSystemPlugin 一致的白名单 / 或 http(s) URL）+ `question`，调 OpenAI 兼容的辅助视觉模型（`image_url` + base64 data URL），返回描述。
- 工具 description 显式列举触发场景（路径/截图/URL/「看一下/识别」），防止模型偷懒不调用——这是 hermes 的提示词工程核心。
- 分析提示词用 hermes 的模板："Fully describe and explain everything about this image, then answer..."。
- 视觉模型配置由 `AemiChatService.BuildVisionConfig()` 提供（设置里可单独配 `VisionModel`/`VisionEndpoint`/`VisionApiKey`，留空则复用当前 Provider）。

### 电脑控制（双轨，跨 Core）

`Aemeath.Core/ComputerControl/`，参考 Microsoft UFO（MIT，`assets/notices/UFO-LICENSE.txt`）。

- **轨 A（默认）`ComputerControlAgent`**：纯 C#，零外部依赖。
  - 感知层 `UiaControlTree`（UIA via `FrameworkReference Microsoft.WindowsDesktop.App` → `System.Windows.Automation`）枚举前台窗口可交互控件、去重、编号；`ScreenCapture` 截图（Win32，不引 WinForms）；`Annotate` 在截图上画编号（移植 UFO 的 annotation）。
  - 动作层 `InputExecutor`：Win32 SendInput 模拟点击/键盘/滚轮，动作集移植自 UFO 的 `api.yaml`（click_input/set_edit_text/keyboard_input/wheel_mouse_input/click_on_coordinates/...）。
  - 规划层：ReAct 循环（截图→控件树→视觉 LLM 决策→执行→观察→下一步），system prompt 移植自 UFO 的 `app_agent.yaml`（one-step JSON 决策 + FINISH/CONTINUE/FAIL/CONFIRM 状态），单 AppAgent 简化（省去 UFO 的 HostAgent/AppAgent 分层）。
- **轨 B（可选）`UfoRunner` + `UfoInstaller`**：通过子进程调用真正的 UFO。`ufo_runner.py`（内嵌资源 `Aemeath.Core.ComputerControl.ufo_runner.py`，启动时由 `BridgeAssetDeployer.DeployUfoRunner` 释放）包装 UFO 的 `SessionFactory`/`SessionPool`。关掉 UFO 的逐步骤 SAFE_GUARD（子进程无 TTY 会死锁），确认责任上移到 Aemeath 侧。
- **后端选择**：`Settings.ComputerControlBackend`（auto/uia/ufo）。`ComputerControlPlugin` 按 `auto`（优先 UFO 已装则用，否则轨 A）/`uia`/`ufo` 路由。
- **确认机制**：`ComputerControlPlugin.computer_control` 工具走 `ToolConfirmationService` 的**任务级前置确认**（会真实操控电脑），与项目其它高风险工具一致。

### MCP（`Aemeath.Core/MCP/`）

- `McpRuntimeService` 支持 stdio / SSE / HTTP 三种 transport，通过 `ModelContextProtocol` 客户端连接。
- 工具被包装成名为 `mcp_<server>_<tool>` 的 KernelFunction，统一接收一个 `argumentsJson` 字符串参数（避免强类型签名问题）。函数名有去重和大小写规范化（`NormalizeFunctionName`）。
- **配置存盘是每服务一文件**：`McpServerStore` 写 `%AppData%\Aemeath\mcp\servers\<id>.json`，已从旧的单文件 `mcp_servers.json` 迁移（迁移用 `.migrated` 标记 + `.bak` 备份防复活）。导入支持标准 `{ "mcpServers": {...} }` 格式和单服务对象。
- **受保护内置服务**：`McpBuiltinRegistry` 把 `filesystem` 标记为受保护——设置界面对用户隐藏、不可删除/禁用、运行时永远强制启用（即使 `Enabled=false` 也加载），防止小白用户误删核心功能。（`memory` 已移除——长期记忆改由 Mem0 提供。）
- **加载是分超时档位 + 每服务独立超时**：后台加载（stdio 30s / http 150s）与手动测试（stdio 60s / http 180s）不同（`GetTimeout`）；每个服务用独立 `CancellationTokenSource`，**单个服务超时只影响它自己**，已成功的工具仍会注册。总预算 200s（`McpReloadTimeoutSeconds`）。
- **连续失败自动禁用**：`TryAutoDisableOnPersistentFailure` 在后台加载（非手动测试）连续失败 3 次后自动 `Enabled=false`，失败计数拼在 `LastError` 前缀里。手动测试不触发禁用。
- stdio 命令在启动子进程前先用 `IsCommandAvailable` 探测（按 PATH + PATHEXT 查找），缺失则给清晰中文提示而非走崩溃路径。`windows_odr` 服务在本机没有 `odr.exe` 时会主动停用。
- uv.exe / bun.exe 体积大不进 Git，首次使用由 `McpDependencyService` 从多个国内镜像降级下载到 `%AppData%\Aemeath\tools\bin`。`Aemeath.Desktop.csproj` 用 `Condition="Exists(...)"` 条件引用 `bin\*.exe`——本地没有也能构建。

## UI 约定

- 颜色/组件令牌集中在 `Aemeath.Desktop/Services/AemiUi.cs`（静态助手），主样式在 `Styles/AemeathTheme.axaml`。新增 UI 尽量复用这些令牌，保持「爱弥斯粉」视觉一致性。
- **响应必须是纯文本，禁止 Markdown**（见系统提示词「回复格式限制」）。聊天渲染层也按纯文本处理。
- 角色口吻要点：自称「小爱」（第三人称），称呼用户「漂泊者」，不主动暴露工具编号/函数名/命令/.exe 名等内部技术痕迹。
- `Behaviors/ImeFixBehavior.cs` 是中文输入法光标修复，`ChatWindow` 体量很大，改动前先定位相关方法。

## 提交与文件注意

- `.gitignore` 已排除 `bin/ obj/ publish/ /bin/ *.exe`（但放行 `tools/**/*.iss`）、`settings.json`、`long_term_memory.json`、`mcp_servers.json`、`*.log` 等。**不要把真实 API Key、用户记忆、发布产物、大体积二进制提交进仓库。**
- Inno Setup 脚本在 `tools/installer.iss`（可选，做安装包用）。
- 最近提交信息多为中文，描述修复内容，风格可保持一致。
