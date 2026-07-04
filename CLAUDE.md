# CLAUDE.md

本文件为 Aemeath Agent 项目的结构化技术文档，供 AI 助手与开发者快速理解项目架构、模块职责、关键类与运行方式。

---

## 一、项目概览

**Aemeath Agent** 是一个写给鸣潮玩家的「爱弥斯」主题 Windows AI 桌宠助手。基于 .NET 8 + Avalonia UI 构建，集成大语言模型对话、桌宠交互、本地知识库、长期记忆（Mem0）、电脑控制（UIA / UFO）和 MCP 工具生态。

- **目标平台**：Windows 10/11 x64
- **运行时**：.NET 8.0
- **UI 框架**：Avalonia UI 12.0.4
- **AI 框架**：Microsoft Semantic Kernel 1.20.0
- **包名**：`Aemeath-agent.exe`（主程序）
- **License**：MIT
- **状态**：持续开发中，无独立测试项目

### 核心定位

让「小爱」（爱弥斯）以桌宠形式陪伴用户，可聊天、可记忆、可调用工具完成电脑操作。涉及删除/覆盖/发送等高风险操作时强制走确认卡片流程。

---

## 二、快速参考

### 构建命令

```powershell
# 依赖还原 + 构建（Debug）
dotnet restore Aemeath.sln
dotnet build Aemeath.sln -c Debug

# 发布自包含版本（win-x64）
dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop

# 或使用脚本
build.bat            # 完整构建并发布
build.bat --no-pause # CI 模式（不暂停）
```

### 验证命令

```powershell
dotnet build Aemeath.sln -c Debug
dotnet test Aemeath.sln   # 暂无测试项目，仅作为基线
```

### 关键路径约定

| 用途 | 路径 |
|---|---|
| 用户配置 | `%AppData%\Aemeath\settings.json` |
| 聊天会话 | `%AppData%\Aemeath\chat_sessions.json` |
| Skill 状态 | `%AppData%\Aemeath\skills_state.json` |
| 用户 Skill | `%AppData%\Aemeath\skills\<name>\` |
| MCP 服务配置 | `%AppData%\Aemeath\mcp\servers\*.json` |
| 旧 MCP 配置（已迁移） | `%AppData%\Aemeath\mcp_servers.json` → `.bak` |
| 日志 | `<exe目录>\log\yyyyMMdd.log`（回退到 `%AppData%\Aemeath\log`） |
| 运行时依赖根 | `<exe目录>\runtime\` |
| MCP 二进制（uv/bun） | `<exe目录>\runtime\bin\` |
| Mem0 venv | `<exe目录>\runtime\Aemeath-Agent\` |
| Mem0 数据 | `<exe目录>\runtime\mem0-data\` |
| UFO 桥接脚本 | `<exe目录>\runtime\ufo-bridge\ufo_runner.py` |
| Whisper 模型 | `%AppData%\Aemeath\whisper\ggml-base.bin` |
| 发布输出 | `publish\Aemeath.Desktop\` |

### 运行权限

**程序以管理员权限运行**（[app.manifest](src/Aemeath.Desktop/app.manifest) 中 `requestedExecutionLevel level="requireAdministrator"`）。

原因：电脑控制功能依赖 `SetCursorPos` / `SendInput` / 跨进程 UIAutomation，这些 API 在普通权限下会被拒绝或失败。仅使用聊天/记忆/知识库等基础功能也会触发 UAC，但无副作用。

---

## 三、仓库结构

```text
Aemeath/
├─ Aemeath.sln                          # 解决方案（4 个项目）
├─ Directory.Build.props                # 全局构建属性（net8.0-windows / Nullable / latest LangVersion）
├─ Directory.Packages.props             # 集中包版本管理（ManagePackageVersionsCentrally）
├─ build.bat                            # 一键构建发布脚本
├─ README.md
├─ LICENSE
├─ 项目评审报告.txt                      # 已知问题清单（共 12 项）
├─ assets/
│  ├─ static/                           # 图标、头像、聊天背景
│  ├─ animations/pet/                   # 桌宠 GIF 动画（daiji/yidong/dianji 等）
│  ├─ fonts/zpix.ttf                    # 像素字体
│  └─ notices/                          # 第三方资源授权与致谢
├─ tools/
│  └─ installer.iss                     # Inno Setup 安装包脚本
└─ src/
   ├─ Aemeath.Core/                     # 核心业务（AI / MCP / 记忆 / 工具 / 知识库 / Skill）
   ├─ Aemeath.Desktop/                  # Avalonia 主程序（聊天窗口 / 设置窗口 / 服务）
   ├─ Aemeath.Pet/                      # 桌宠窗口、动画、跟随
   └─ Aemeath.Speech/                   # 语音识别（Windows Speech + Whisper.net）
```

---

## 四、解决方案架构

解决方案包含 4 个项目，依赖关系如下：

```text
Aemeath.Desktop (WinExe, 主入口)
   ├─> Aemeath.Core
   ├─> Aemeath.Pet ──> Aemeath.Core
   └─> Aemeath.Speech (无项目引用，独立)
```

| 项目 | 类型 | RootNamespace | 职责 |
|---|---|---|---|
| Aemeath.Core | ClassLib | `Aemeath.Core` | AI 聊天、配置、MCP、记忆、知识库、Skill、工具插件、电脑控制 |
| Aemeath.Desktop | WinExe | `Aemeath.Desktop` | Avalonia 主程序、聊天窗口、设置窗口、托盘、服务编排 |
| Aemeath.Pet | ClassLib | `Aemeath.Pet` | 桌宠窗口、GIF 动画、跟随、边缘吸附、右键菜单 |
| Aemeath.Speech | ClassLib | `Aemeath.Speech` | 语音识别（Windows 原生 + Whisper.net 兜底） |

---

## 五、Aemeath.Core 模块详解

核心业务层，无 UI 依赖。所有功能以 Plugin / Service 形式注入 Semantic Kernel。

### 5.1 AI 子模块（`AI/`）

#### 关键类

| 类 | 文件 | 职责 |
|---|---|---|
| `IChatService` | [IChatService.cs](src/Aemeath.Core/AI/IChatService.cs) | 聊天服务抽象接口，定义 `SendMessageAsync` / `SendMessageStreamingAsync` / `ClearHistory` / `SwitchProviderAsync` / `RegisterTool` |
| `AemiChatService` | [AemiChatService.cs](src/Aemeath.Core/AI/AemiChatService.cs) | `IChatService` 主实现，编排 Kernel / Skill / 知识库 / MCP / 工具插件 / Mem0 配置；持有 `McpRuntimeService` / `SkillService` / `KnowledgeBaseService` / `ToolConfirmationService` |
| `KernelMixinBase` | [KernelMixinBase.cs](src/Aemeath.Core/AI/KernelMixinBase.cs) | Kernel 抽象基类，管理 `ChatHistory` / 附件构建 / 图片压缩 / 流式与非流式发送 / 插件注册 |
| `OpenAIKernelMixin` | [OpenAIKernelMixin.cs](src/Aemeath.Core/AI/OpenAIKernelMixin.cs) | OpenAI-compatible Provider 实现，持有 `HttpClient`（5 分钟超时，避免 Cloudflare 524） |
| `OpenAIResponseNormalizationHandler` | [OpenAIResponseNormalizationHandler.cs](src/Aemeath.Core/AI/OpenAIResponseNormalizationHandler.cs) | `DelegatingHandler`，规范化 OpenAI 兼容端点的响应 |
| `StreamingThinkCleaner` | [StreamingThinkCleaner.cs](src/Aemeath.Core/AI/StreamingThinkCleaner.cs) | 流式输出时清洗 `<think>` 块（推理模型思考过程） |
| `ChatAttachment` / `ChatAttachmentKind` | [ChatAttachment.cs](src/Aemeath.Core/AI/ChatAttachment.cs) | 聊天附件模型（Text / Image / Other） |
| `AemiSystemPrompt` | [AI/Prompts/AemiSystemPrompt.cs](src/Aemeath.Core/AI/Prompts/AemiSystemPrompt.cs) | 系统提示词常量：`CapabilityBase`（能力基座）+ `FallbackPersona`（降级人格） |

#### 聊天流程

1. `AemiChatService.InitializeFromSettings()` 加载 Skill 人格 + 知识库条目，拼接 `persona + CapabilityBase` 作为系统提示词
2. 构造 `OpenAIKernelMixin`，注册内置工具插件（filesystem / screenshot / browser / reminder / knowledge / vision / computer_control / mcp_local）
3. `SendMessageAsync` / `SendMessageStreamingAsync` 调用 `EnrichMessageWithKnowledge` 注入知识库命中与 MCP 工具清单，再走 Kernel 调用
4. 非流式返回前经 `FormatAemiResponse` 清洗 think 标签（**注意：流式分支仅用 `StreamingThinkCleaner` 增量清洗，行为不完全一致，见已知问题 6**）
5. `ReloadMcpToolsAsync` 后台并发加载所有启用的 MCP 服务，整体超时 200s，单服务独立超时

#### 视觉能力判定

`ResolveVisionCapability` 决定图片以 `ImageContent` 直接发送还是走 `vision_analyze` 工具：
- 优先查 `ProviderModel.SupportsImageInput`（由 `/models` API 探测）
- 未探测时按模型名匹配兜底（gpt-4o / claude-3 / gemini / qwen-vl 等返回 true；gpt-3.5 / deepseek-r1 / o1-mini 等返回 false）
- 默认 true

### 5.2 Configuration 子模块（`Configuration/`）

| 类 | 文件 | 职责 |
|---|---|---|
| `Settings` | [Settings.cs](src/Aemeath.Core/Configuration/Settings.cs) | 配置 POCO，含 Provider / 模型 / 桌宠 / 视觉模型 / Mem0 / 电脑控制后端等字段 |
| `ApiKey` | [ApiKey.cs](src/Aemeath.Core/Configuration/ApiKey.cs) | Provider 凭据模型（Key / Endpoint / ModelId / Models 列表 / 连接状态） |
| `SettingsService` | [SettingsService.cs](src/Aemeath.Core/Configuration/SettingsService.cs) | 配置读写、Provider CRUD、模型列表管理；**API Key 用 DPAPI（`ProtectedData.Protect`）按当前用户加密落盘** |
| `ProviderProbeService` | [ProviderProbeService.cs](src/Aemeath.Core/Configuration/ProviderProbeService.cs) | 探测 Provider 端点能力（`/models` 列表、模型视觉/推理能力推断） |

`SettingsService` 关键行为：
- `NormalizeProvider`：Provider 名一律小写规范化
- `TryEncrypt` / `TryDecrypt`：DPAPI 加密失败时记日志但返回原值（避免设置整体无法保存，SEC-006 已加可观测痕迹）
- `MigrateProviderModels`：旧配置兼容，把 `ModelId` 补进 `Models` 列表
- `Save` 时深拷贝快照再加密，避免内存对象被覆盖

### 5.3 MCP 子模块（`MCP/`）

| 类 | 文件 | 职责 |
|---|---|---|
| `McpServerConfig` | [McpServerConfig.cs](src/Aemeath.Core/MCP/McpServerConfig.cs) | MCP 服务配置（Id / Name / Transport / Command / Args / Env / Url / Headers / 状态字段） |
| `McpTransportType` | McpServerConfig.cs | 传输类型枚举：`Stdio` / `Sse` / `Http` |
| `McpServerStore` | [McpServerStore.cs](src/Aemeath.Core/MCP/McpServerStore.cs) | 持久化层：每服务一个 JSON 文件存于 `%AppData%\Aemeath\mcp\servers\`；含旧配置迁移、路径修复、文件锁 |
| `McpRuntimeService` | [McpRuntimeService.cs](src/Aemeath.Core/MCP/McpRuntimeService.cs) | 运行时：并发加载所有启用服务、构建 `KernelPlugin`、调用工具、超时分级、连续失败自动禁用 |
| `McpBuiltinRegistry` | [McpBuiltinRegistry.cs](src/Aemeath.Core/MCP/McpBuiltinRegistry.cs) | 受保护服务注册表（`filesystem` 不可删/不可禁/强制加载） |
| `McpChatPlugin` | [McpChatPlugin.cs](src/Aemeath.Core/MCP/McpChatPlugin.cs) | 内置 MCP 工具集（local tools） |
| `McpDependencyService` | [McpDependencyService.cs](src/Aemeath.Core/MCP/McpDependencyService.cs) | 下载 `uv.exe` / `bun.exe` 到 `runtime/bin`（多镜像降级） |

#### MCP 加载策略

- `BuildEnabledPluginAsync` 并发加载所有 `Enabled=true` 的服务（受保护服务强制加载）
- 每服务独立超时 token：stdio 30s / http 150s（后台），60s / 180s（手动测试）
- 连接超时按 40% / 工具列表超时按 60% 拆分，避免单段耗尽
- HTTP/SSE 首次握手失败重试 1 次
- 连续失败 3 次自动禁用（错误信息前缀 `[连续失败 N/3]`）
- 命令存在性预检：`IsCommandAvailable` 在 PATH 中按 PATHEXT 查找，避免子进程崩溃路径
- 凭据脱敏：`RedactUrlSecrets` / `RedactText` 抹掉 URL 与异常文本中的 token / api_key

### 5.4 Memory 子模块（`Memory/`）

| 类 | 文件 | 职责 |
|---|---|---|
| `Mem0ConnectionConfig` | [Mem0Client.cs](src/Aemeath.Core/Memory/Mem0Client.cs) | Mem0 连接配置（LLM / Embedding / Vector / 超时） |
| `Mem0Scope` | Mem0Client.cs | 记忆作用域：`GlobalUser`（user_id=drifter, agent_id=aemi）/ `ForSession(sessionId)`（额外 run_id） |
| `Mem0Client` | [Mem0Client.cs](src/Aemeath.Core/Memory/Mem0Client.cs) | 与 `mem0_bridge.py` 子进程通信的 JSON-RPC 客户端（stdin/stdout 行协议） |
| `MemoryOrchestrator` | [MemoryOrchestrator.cs](src/Aemeath.Core/Memory/MemoryOrchestrator.cs) | 编排层：每轮 `AddTurnAsync` 写入、发送前 `BuildRelevantMemoryBlockAsync` 检索并拼提示词 |
| `Mem0DependencyService` | [Mem0DependencyService.cs](src/Aemeath.Core/Memory/Mem0DependencyService.cs) | 用 `uv` 创建独立 venv 安装 `mem0ai` + `qdrant-client` |
| `mem0_bridge.py` | [Memory/mem0_bridge.py](src/Aemeath.Core/Memory/mem0_bridge.py) | 内嵌 Python 桥接脚本（运行时释放到 `mem0-data/bridge/`） |

#### 记忆流程

1. 用户配置 `Mem0PythonPath` + 启用 `Mem0Enabled`
2. `AemiChatService.BuildMem0Config()` 基于当前 Provider 构造 LLM + Embedding 配置
3. `MemoryOrchestrator` 惰性拉起 `Mem0Client`（首次使用时启动 Python 子进程，握手 `__hello__`）
4. 每轮对话完成 → `AddTurnAsync` 把 user+assistant 消息喂给 Mem0（Mem0 内部 LLM 自动抽取事实）
5. 下次发送前 → `BuildRelevantMemoryBlockAsync` 同时检索会话记忆（topK=4）+ 全局用户记忆（topK=6），去重排序后拼成提示词块
6. 进程级故障（桥接崩溃）进入短路态，避免每条消息重试

### 5.5 Knowledge 子模块（`Knowledge/`）

| 类 | 文件 | 职责 |
|---|---|---|
| `KnowledgeBaseEntry` | [KnowledgeBaseEntry.cs](src/Aemeath.Core/Knowledge/KnowledgeBaseEntry.cs) | 知识条目模型（Id / Title / Category / Content / Aliases / SourceUrl） |
| `KnowledgeBaseService` | [KnowledgeBaseService.cs](src/Aemeath.Core/Knowledge/KnowledgeBaseService.cs) | 加载内嵌 `knowledge_base.zh-CN.json` + 接受 Skill 注入的额外条目；关键词评分检索 |
| `KnowledgeBasePlugin` | [KnowledgeBasePlugin.cs](src/Aemeath.Core/Knowledge/KnowledgeBasePlugin.cs) | 暴露 `knowledge_search` KernelFunction 给模型主动调用 |

知识库是**本地静态**的，覆盖爱弥斯身份/背景/性格/外貌、星炬学院、电子幽灵等鸣潮设定。检索为关键词评分（非向量），按 Title/Category/Aliases/Content 命中权重打分。Skill 提供的条目与内置条目互补，重载 Skill 时清空旧的外部条目再注入新的。

### 5.6 Skills 子模块（`Skills/`）

| 类 | 文件 | 职责 |
|---|---|---|
| `SkillManifest` | [SkillManifest.cs](src/Aemeath.Core/Skills/SkillManifest.cs) | Skill 元数据（Name / Description / IsBuiltin / Enabled / TriggerWords） |
| `SkillPackage` | [SkillPackage.cs](src/Aemeath.Core/Skills/SkillPackage.cs) | 加载后的 Skill 包（Manifest + PersonaPrompt + KnowledgeEntries） |
| `SkillLoader` | [SkillLoader.cs](src/Aemeath.Core/Skills/SkillLoader.cs) | 从内嵌资源（`Aemeath.Skills.<name>.*`）和 `%AppData%\Aemeath\skills\` 加载 |
| `SkillService` | [SkillService.cs](src/Aemeath.Core/Skills/SkillService.cs) | Skill 管理：加载 / 启用切换 / 删除 / 导入；状态持久化在 `skills_state.json` |

#### 内置 Aemeath Skill

位于 [Skills/AemeathSkill/](src/Aemeath.Core/Skills/AemeathSkill/)：
- `SKILL.md` — 入口与 frontmatter
- `profile.md` — 角色档案
- `personality.md` — 人格设定
- `interaction.md` — 交互风格
- `memory.md` — 记忆规则
- `relations.md` — 关系设定
- `conflicts.md` — 冲突处理

内置 Skill 恒启用、不可删除/禁用；用户 Skill 可在面板切换。Skill 的知识条目会并入 `KnowledgeBaseService` 参与检索。

### 5.7 Tools 子模块（`Tools/`）

| 类 | 文件 | 职责 |
|---|---|---|
| `ToolConfirmationService` | [ToolConfirmationService.cs](src/Aemeath.Core/Tools/ToolConfirmationService.cs) | 高风险操作确认中枢：发起到确认到执行的完整事件流 |
| `PendingToolAction` | [PendingToolAction.cs](src/Aemeath.Core/Tools/PendingToolAction.cs) | 待确认动作模型（支持同步/异步闭包） |
| `FileSystemPlugin` | [FileSystemPlugin.cs](src/Aemeath.Core/Tools/FileSystemPlugin.cs) | 文件读写/搜索/列目录，**路径白名单：用户主目录 + 临时目录，禁止进入 `%AppData%\Aemeath`** |
| `BrowserPlugin` | [BrowserPlugin.cs](src/Aemeath.Core/Tools/BrowserPlugin.cs) | 打开浏览器/搜索/打开本机应用 |
| `ScreenshotPlugin` | [ScreenshotPlugin.cs](src/Aemeath.Core/Tools/ScreenshotPlugin.cs) | 屏幕截图 |
| `ReminderPlugin` | [ReminderPlugin.cs](src/Aemeath.Core/Tools/ReminderPlugin.cs) | 定时提醒（基于 Timer.Elapsed） |
| `VisionPlugin` | [VisionPlugin.cs](src/Aemeath.Core/Tools/VisionPlugin.cs) | `vision_analyze` 工具，调视觉模型分析图片 |

#### 确认流程

1. 工具遇高风险操作 → `RequestConfirmation(title, description, closure)` 返回 `AEMEATH_PENDING_CONFIRMATION:<id>` marker
2. marker 作为工具结果回到模型 → UI 订阅 `PendingActionCreated` 事件弹确认卡片
3. 用户点确认 → `ConfirmAsync(id)`：闭包在**后台线程**执行（绝不阻塞 UI）
4. 执行完成触发 `PendingActionCompleted` → UI 把结果回填到聊天

`FileSystemPlugin.WriteFile` 在文件已存在时强制走确认；`ComputerControlPlugin` 走 `isLongRunning=true` 异步闭包。

### 5.8 ComputerControl 子模块（`ComputerControl/`）

| 类 | 文件 | 职责 |
|---|---|---|
| `ComputerControlPlugin` | [ComputerControlPlugin.cs](src/Aemeath.Core/ComputerControl/ComputerControlPlugin.cs) | `computer_control` KernelFunction 入口，任务级前置确认 + 后端选择 |
| `ComputerControlAgent` | [ComputerControlAgent.cs](src/Aemeath.Core/ComputerControl/ComputerControlAgent.cs) | 轨 A：C# UIAutomation + 视觉 LLM 的 ReAct Agent（移植自 UFO app_agent.yaml） |
| `UfoRunner` | [UfoRunner.cs](src/Aemeath.Core/ComputerControl/UfoRunner.cs) | 轨 B：调用 UFO（Microsoft）Python 子进程 |
| `UfoInstaller` | — | UFO 安装/检测 |
| `ScreenCapture` | [ScreenCapture.cs](src/Aemeath.Core/ComputerControl/ScreenCapture.cs) | 屏幕截图 |
| `UiaControlTree` | [UiaControlTree.cs](src/Aemeath.Core/ComputerControl/UiaControlTree.cs) | UIA 控件树枚举 + 截图标注（按类型颜色编码） |
| `InputExecutor` | [InputExecutor.cs](src/Aemeath.Core/ComputerControl/InputExecutor.cs) | 鼠标点击/键盘输入/滚轮/拖拽（SendInput） |
| `AppLauncher` | [AppLauncher.cs](src/Aemeath.Core/ComputerControl/AppLauncher.cs) | 解析应用快捷方式/可执行路径 |
| `WeChatDirectController` | [WeChatDirectController.cs](src/Aemeath.Core/ComputerControl/WeChatDirectController.cs) | 微信直控（绕过 UIA，WeChat 4.x 自定义渲染 UI） |
| `Win32Interop` | [Win32Interop.cs](src/Aemeath.Core/ComputerControl/Win32Interop.cs) | Win32 P/Invoke（DPI 感知、窗口最小化、光标定位） |
| `ufo_runner.py` | [ComputerControl/ufo_runner.py](src/Aemeath.Core/ComputerControl/ufo_runner.py) | UFO 桥接脚本（运行时释放到 `runtime/ufo-bridge/`） |

#### 后端选择

由 `Settings.ComputerControlBackend` 决定：
- `auto`（默认）：若 UFO 可用则用 UFO，否则回退轨 A
- `uia`：强制轨 A
- `ufo`：强制轨 B

#### 轨 A Agent 算法

每步循环（最多 30 步）：
1. 截图全屏 + UIA 枚举前台控件 + 标号生成带标注截图
2. 调视觉 LLM（OpenAI 兼容，streaming 模式避免 Cloudflare 524）传入截图 + 控件列表 + 历史
3. LLM 返回 JSON `{observation, thought, action:{function, args, status}, comment}`
4. 执行 action（`click_input` / `set_edit_text` / `keyboard_input` / `wheel_mouse_input` / `click_on_coordinates` / `drag_on_coordinates` / `launch_application` / `minimize_all_windows` / `summary` / `texts`）
5. `status`：`FINISH` 完成 / `CONTINUE` 继续 / `FAIL` 报错 / `CONFIRM` 记录后继续
6. 重复操作检测：连续 3 步相同 function + 关键参数签名 → 中止
7. 图片体积治理：三轮重试 1024→800→640 px

涉及微信任务时优先走 `WeChatDirectController`（UIA 对微信几乎无效），未处理才落回视觉 Agent。

### 5.9 公共工具

| 类 | 文件 | 职责 |
|---|---|---|
| `RuntimePaths` | [RuntimePaths.cs](src/Aemeath.Core/RuntimePaths.cs) | 集中解析运行时数据/依赖路径（exe 同级 `runtime/`） |
| `OpenAIUrlHelper` | [OpenAIUrlHelper.cs](src/Aemeath.Core/OpenAIUrlHelper.cs) | OpenAI 兼容端点 URL 规范化（截到 `/v1` 末尾） |

---

## 六、Aemeath.Desktop 模块详解

Avalonia 主程序层，负责 UI 编排与服务调度。

### 6.1 入口与生命周期

| 文件 | 职责 |
|---|---|
| [Program.cs](src/Aemeath.Desktop/Program.cs) | `Main` 入口：单实例互斥锁（`Local\Aemeath.Desktop.SingleInstance`）、全局异常处理、启动 Avalonia |
| [App.axaml.cs](src/Aemeath.Desktop/App.axaml.cs) | 应用生命周期：构造 `SettingsService` / `AemiChatService` / `PetWindow` / `ChatWindow` / `ConfigWindow`；订阅 Reminder 事件；管理 MCP 后台重载；统一窗口关闭与释放 |
| [App.axaml](src/Aemeath.Desktop/App.axaml) | 应用资源与主题入口 |
| [app.manifest](src/Aemeath.Desktop/app.manifest) | `requireAdministrator` UAC 提权 |

#### 启动流程

1. `AppLogger.Initialize()` 初始化日志
2. 单实例互斥锁检查
3. `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`
4. `App.OnFrameworkInitializationCompleted`:
   - 创建 `SettingsService` / `AemiChatService`
   - 注入 UI 线程调度器 + Reminder 事件转发
   - 创建 `PetWindow`（主窗口）
   - `BridgeAssetDeployer.DeployUfoRunner()` 释放 UFO 桥接脚本
   - `StartMcpBackgroundReload()` 后台并发加载 MCP 工具

#### 窗口管理

`App` 持有 `_petWindow` / `_chatWindow` / `_configWindow` 三个引用，分别通过 `OpenChatWindow` / `OpenConfigWindow` / `OpenConfigAtMcpTab` 打开。桌宠关闭时若启用 `MinimizeToTray` 则隐藏而非退出。

`OnChatActivityChanged` 把聊天状态映射到桌宠动画：
- `Sending` → Running
- `VoiceListening` → Waiting
- `ToolWaiting` → Waiting
- `Completed` → Review（1.4s）
- `Failed` → Failed（1.6s）

### 6.2 Services

| 类 | 文件 | 职责 |
|---|---|---|
| `AppLogger` | [AppLogger.cs](src/Aemeath.Desktop/Services/AppLogger.cs) | 简易文件日志（按天分文件，线程安全） |
| `ChatSessionStore` | [ChatSessionStore.cs](src/Aemeath.Desktop/Services/ChatSessionStore.cs) | 聊天会话持久化到 `chat_sessions.json`（会话列表 + 消息记录） |
| `AemiUi` | [AemiUi.cs](src/Aemeath.Desktop/Services/AemiUi.cs) | UI 设计系统：颜色常量（粉色系）+ Surface/Text/Badge/Button 工厂方法 + 状态点配色 |
| `AttachmentService` | [AttachmentService.cs](src/Aemeath.Desktop/Services/AttachmentService.cs) | 聊天附件管理 |
| `AutoStartService` | [AutoStartService.cs](src/Aemeath.Desktop/Services/AutoStartService.cs) | 开机自启（注册表 / 启动文件夹） |
| `BridgeAssetDeployer` | [BridgeAssetDeployer.cs](src/Aemeath.Desktop/Services/BridgeAssetDeployer.cs) | 释放 UFO 桥接脚本到 `runtime/ufo-bridge/` |
| `ImeFixBehavior` | [Behaviors/ImeFixBehavior.cs](src/Aemeath.Desktop/Behaviors/ImeFixBehavior.cs) | Avalonia IME 修复 |

### 6.3 Views

| 文件 | 职责 |
|---|---|
| [PetWindow.axaml](src/Aemeath.Pet/PetWindow.axaml) | 桌宠窗口 XAML（实际在 Aemeath.Pet 项目） |
| [ChatWindow.axaml(.cs)](src/Aemeath.Desktop/Views/ChatWindow.axaml.cs) | 聊天主窗口：消息列表、输入框、流式输出、附件、确认卡片、语音模式、Provider 切换 |
| [ConfigWindow.axaml(.cs)](src/Aemeath.Desktop/Views/ConfigWindow.axaml.cs) | 设置中心，4 个 Tab：提供商配置(0) / 记忆管理(1) / 电脑控制(2) / MCP 配置(3) |
| [McpConfigPanel.axaml(.cs)](src/Aemeath.Desktop/Views/McpConfigPanel.axaml.cs) | MCP 配置面板 |
| [McpImportWindow.axaml(.cs)](src/Aemeath.Desktop/Views/McpImportWindow.axaml.cs) | MCP 配置导入窗口 |
| [SkillConfigPanel.axaml(.cs)](src/Aemeath.Desktop/Views/SkillConfigPanel.axaml.cs) | Skill 管理面板 |

### 6.4 Styles

[AemeathTheme.axaml](src/Aemeath.Desktop/Styles/AemeathTheme.axaml) 定义应用主题（粉色系，爱弥斯配色）。

---

## 七、Aemeath.Pet 模块详解

桌宠窗口与交互层。

| 类 | 文件 | 职责 |
|---|---|---|
| `PetWindow` | [PetWindow.axaml.cs](src/Aemeath.Pet/PetWindow.axaml.cs) | 桌宠主窗口：拖拽、双击打开聊天、右键菜单、跟随、边缘吸附、气泡台词、闲置问候、临时状态播放 |
| `PetViewModel` | [PetViewModel.cs](src/Aemeath.Pet/PetViewModel.cs) | `ObservableObject` 视图模型；`PetState` 枚举（Idle/Follow/FollowLeft/Click/Wave/Jump/Failed/Waiting/Running/Review） |
| `GifAnimationService` | [Services/GifAnimationService.cs](src/Aemeath.Pet/Services/GifAnimationService.cs) | GIF 帧解码与状态切换 |
| `FollowService` | [Services/FollowService.cs](src/Aemeath.Pet/Services/FollowService.cs) | 鼠标跟随算法（缓动因子 0.04，停止阈值 8px） |
| `ParticleEffect` | [Effects/ParticleEffect.cs](src/Aemeath.Pet/Effects/ParticleEffect.cs) | 粒子特效 |

### 桌宠状态

- 加载 9 种 GIF：`daiji`(Idle) / `yidong`(Follow) / `dianji`(Click) / `aemeath-mini-waving`(Wave) / `aemeath-mini-jumping`(Jump) / `aemeath-mini-failed`(Failed) / `aemeath-mini-waiting`(Waiting) / `aemeath-mini-running-left`(FollowLeft) / `aemeath-mini-review`(Review)（Running 复用 daiji）
- 优先级：临时状态 > 活动状态 > 跟随/闲置基础状态
- 闲置问候：每 30 秒检查，90 秒无操作时随机气泡
- 单击/双击区分：350ms 延迟判定，双击直接打开聊天，单击播放 Click 动画

---

## 八、Aemeath.Speech 模块详解

语音识别层，独立无项目引用。

| 类 | 文件 | 职责 |
|---|---|---|
| `SpeechService` | [SpeechService.cs](src/Aemeath.Speech/SpeechService.cs) | 双引擎语音识别：Windows 原生 `SpeechRecognizer` 优先，Whisper.net 兜底 |

### 引擎选择

1. **WindowsNative**（优先）：`Windows.Media.SpeechRecognition.SpeechRecognizer`，连续识别模式
2. **Whisper**（兜底）：NAudio 录音（16kHz 单声道）→ Whisper.net `ggml-base` 模型转写

模型缓存在 `%AppData%\Aemeath\whisper\ggml-base.bin`，首次使用时从 WhisperGgmlDownloader 下载。识别结果统一经 `TraditionalToSimplified`（`LCMapStringEx`）转简体中文。

---

## 九、依赖关系

### 9.1 NuGet 包（集中管理于 [Directory.Packages.props](Directory.Packages.props)）

| 包 | 版本 | 用于项目 | 用途 |
|---|---|---|---|
| Avalonia | 12.0.4 | Desktop, Pet | UI 框架 |
| Avalonia.Desktop | 12.0.4 | Desktop, Pet | 桌面平台支持 |
| Avalonia.Themes.Fluent | 12.0.4 | Desktop | Fluent 主题 |
| Avalonia.Fonts.Inter | 12.0.4 | Desktop | 内置字体 |
| CommunityToolkit.Mvvm | 8.2.2 | Desktop, Pet | MVVM 源生成器（`ObservableProperty` / `RelayCommand`） |
| Microsoft.SemanticKernel | 1.20.0 | Core | AI 编排框架 |
| Microsoft.SemanticKernel.Connectors.OpenAI | 1.20.0 | Core | OpenAI 连接器 |
| ModelContextProtocol.Core | 1.3.0 | Core | MCP 客户端 |
| System.Security.Cryptography.ProtectedData | 8.0.0 | Core | DPAPI 凭据加密 |
| System.Text.Json | 10.0.6 | Core | JSON 序列化 |
| SixLabors.ImageSharp | 3.1.11 | Core, Desktop, Pet | 图像处理 |
| UIAComWrapper | 1.1.0.14 | Core | UIAutomation 托管封装 |
| System.Drawing.Common | 8.0.0 | Core | 截图 + 标注 |
| Whisper.net | 1.8.1 | Speech | Whisper 推理 |
| Whisper.net.Runtime | 1.8.1 | Speech | Whisper 运行时 |
| NAudio | 2.2.1 | Speech | 音频录制 |
| Microsoft.Windows.SDK.NET | 10.0.18362.6-preview | Speech | Windows Speech API |
| Microsoft.WindowsDesktop.App | (FrameworkReference) | Core | 引入 `System.Windows.Automation` / `WindowsBase`（不开启 UseWPF） |

### 9.2 项目引用

- `Aemeath.Desktop` → `Aemeath.Core` + `Aemeath.Pet` + `Aemeath.Speech`
- `Aemeath.Pet` → `Aemeath.Core`
- `Aemeath.Speech` 无项目引用

### 9.3 外部运行时依赖（运行时下载，不入仓库）

- `uv.exe` / `uvx.exe` / `bun.exe` — MCP 服务运行时（通过 `McpDependencyService` 多镜像下载到 `runtime/bin/`）
- Python venv — Mem0 运行环境（`Mem0DependencyService` 用 `uv` 创建在 `runtime/Aemeath-Agent/`）
- UFO 源码（可选）— 用户手动安装的电脑控制轨 B 后端
- Whisper `ggml-base.bin` — 首次使用语音时自动下载

### 9.4 第三方参考项目

详见 [assets/notices/THIRD_PARTY_NOTICES.md](assets/notices/THIRD_PARTY_NOTICES.md)：
- Ameath（MIT）— UI 风格参考 + Zpix 字体
- aemeath-mini-codex-pet（MIT）— 桌宠 GIF 资源
- Aemeath-skill（MIT）— 爱弥斯语气与知识库
- Mem0（Apache 2.0）— 长期记忆核心（通过 Python 桥接调用 SDK）
- Hermes-Agent（MIT）— VisionPlugin 实现思路参考
- UFO（MIT）— 电脑控制 Agent 逻辑参考 + 可选后端

---

## 十、项目运行方式

### 10.1 开发环境准备

1. 安装 .NET SDK 8.0+（https://dotnet.microsoft.com/download）
2. Windows 10/11 x64
3. 可选：Inno Setup（制作安装包）
4. 推荐：Visual Studio 2022 或 VS Code + C# Dev Kit

### 10.2 构建运行

```powershell
# 在仓库根目录
dotnet restore Aemeath.sln
dotnet build Aemeath.sln -c Debug
dotnet run --project src/Aemeath.Desktop/Aemeath.Desktop.csproj
```

### 10.3 发布自包含版本

```powershell
dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop
```

发布后运行 `publish\Aemeath.Desktop\Aemeath-agent.exe`（**会触发 UAC 提权**）。

### 10.4 一键脚本

```bat
build.bat            :: restore + build Release + publish
build.bat --no-pause :: 同上但末尾不暂停（CI 友好）
```

### 10.5 安装包

使用 Inno Setup 编译 [tools/installer.iss](tools/installer.iss)：
- 安装到 `{localappdata}\Aemeath`（用户目录，避免管理员权限）
- `PrivilegesRequired=lowest`（安装器本身不需提权，但程序运行时仍需）

### 10.6 首次使用配置

1. 启动程序（UAC 同意）
2. 双击桌宠 → 打开聊天窗口 → 右键 → 打开系统配置
3. **提供商配置 Tab**：新增 Provider，填 Endpoint / API Key / 默认模型，测试连接或获取模型列表
4. **MCP 配置 Tab**：点击「检测/下载 MCP 依赖」下载 `uv.exe` / `bun.exe`
5. **记忆管理 Tab**：安装 Mem0 依赖（自动创建 venv），启用长期记忆
6. **电脑控制 Tab**：配置辅助视觉模型（需支持图片输入，如 gpt-4o）
7. **界面与行为**：调整桌宠大小、透明度、气泡台词、边缘吸附、跟随
8. 回到桌面双击小爱开始聊天

---

## 十一、关键流程图

### 11.1 聊天消息流

```text
用户输入
  ↓
ChatWindow.SendMessageAsync
  ↓
AemiChatService.SendMessageStreamingAsync
  ├─ EnrichMessageWithKnowledge
  │   ├─ KnowledgeBaseService.Search（关键词命中）
  │   └─ McpRuntimeService.GetLoadedToolSummary（注入工具清单）
  ├─ KernelMixinBase.SendMessageStreamingAsync
  │   ├─ BuildUserContentItemsAsync（图片压缩到 2048px + JPEG 85%）
  │   ├─ ChatHistory.AddUserMessage
  │   ├─ OpenAIPromptExecutionSettings { ToolCallBehavior = AutoInvokeKernelFunctions }
  │   └─ IChatCompletionService.GetStreamingChatMessageContentsAsync
  │       └─ 工具自动调用（filesystem / browser / vision / computer_control / mcp_*）
  │           └─ 高风险操作走 ToolConfirmationService.RequestConfirmation
  │               └─ UI 弹卡片 → ConfirmAsync 后台执行 → PendingActionCompleted
  └─ StreamingThinkCleaner 增量清洗 <think>
  ↓
ChatWindow 渲染流式 chunk + 桌宠动画反馈
  ↓
完成后 MemoryOrchestrator.AddTurnAsync（如启用 Mem0）
```

### 11.2 MCP 工具加载

```text
App.StartMcpBackgroundReload (启动时)
  ↓
AemiChatService.ReloadMcpToolsAsync
  ├─ 等待 _mcpReloadLock（200s 总预算）
  ├─ McpRuntimeService.BuildEnabledPluginAsync
  │   ├─ DisposeClientsAsync（清理旧客户端）
  │   ├─ DisableUnsupportedOdrIfNeeded（本机无 odr.exe 则停用）
  │   ├─ 并发 LoadServerAsync（每服务独立超时）
  │   │   ├─ IsCommandAvailable 预检（stdio）
  │   │   ├─ CreateClientWithRetryAsync（HTTP/SSE 重试 1 次）
  │   │   ├─ ListToolsAsync
  │   │   └─ TryAutoDisableOnPersistentFailure（连续 3 次失败禁用）
  │   └─ KernelPluginFactory.CreateFromFunctions("mcp", functions)
  └─ _currentKernel.ReplacePlugin(plugin)（切 UI 线程执行）
```

### 11.3 电脑控制（轨 A）

```text
LLM 调用 computer_control(task)
  ↓
ComputerControlPlugin.ControlComputerAsync
  ├─ ToolConfirmationService.RequestConfirmation（任务级前置确认）
  │   └─ UI 弹卡片，用户确认后后台执行
  └─ RunAgentAsync
      ├─ MentionsWeChat? → WeChatDirectController.TryHandleAsync
      └─ ComputerControlAgent.RunAsync
          └─ 循环（最多 30 步）：
              1. ScreenCapture.CaptureFullScreen
              2. UiaControlTree.CaptureForeground + Annotate
              3. DecideAsync（视觉 LLM streaming，三轮重试 1024/800/640px）
              4. ExecuteAction（click_input / set_edit_text / ...）
              5. 重复检测（连续 3 步同签名 → 中止）
              6. status FINISH/FAIL/CONTINUE
```

---

## 十二、安全与隐私

### 12.1 凭据保护

- API Key / Azure Speech Key / Vision API Key 通过 **Windows DPAPI**（`ProtectedData.Protect`，`DataProtectionScope.CurrentUser`）按当前用户加密后落盘到 `settings.json`（SEC-005）
- DPAPI 加密失败时记日志但不静默降级为明文（SEC-006）
- 旧版本明文存储的凭据在 `TryDecrypt` 失败时原样返回，保持向后兼容

### 12.2 文件系统沙箱

`FileSystemPlugin.IsAllowedPath` 限制：
- **白名单**：用户主目录（`UserProfile`）+ 临时目录（`Path.GetTempPath()`）
- **黑名单**：明确禁止进入 `%AppData%\Aemeath`（含 settings / 记忆 / mcp 配置等敏感数据）（SEC-003）
- 路径必须为绝对路径且落在白名单根目录内

### 12.3 高风险操作确认

- 文件覆盖写入
- 电脑控制任务（会真实点击/输入用户电脑）
- 其他高风险 KernelFunction 调用

确认闭包**永远在后台线程执行**，绝不阻塞 UI 线程。

### 12.4 错误信息脱敏

`McpRuntimeService.RedactUrlSecrets` / `RedactText` 抹掉 URL 查询串与异常文本中的 `token` / `key` / `secret` / `password` / `apikey` / `bearer` 等敏感参数，避免凭据被写进错误日志落盘（SEC-008 / SEC-015）。

### 12.5 数据本地化

所有用户数据保存在本机（`%AppData%\Aemeath` + `<exe>\runtime`），不上传任何云端。Mem0 向量库使用本地 Qdrant。

### 12.6 仓库排除项

`.gitignore` 排除：本地设置、API Key、长期记忆 JSON、MCP 本地配置、日志、构建产物、发布目录、大体积运行时二进制。

---

## 十三、已知问题（来自项目评审报告）

详见 [项目评审报告.txt](项目评审报告.txt)，共 12 项。摘要：

### 严重（6 项）

1. **`SelectMcpTab` 索引错位**：[ConfigWindow.axaml.cs](src/Aemeath.Desktop/Views/ConfigWindow.axaml.cs) 设置 `SelectedIndex = 2` 实际高亮"电脑控制"Tab，应改为 `3`
2. **`MicrophoneHandler.RecordAsync` 死代码**：恒返回 null，无外部引用，应删除
3. **`ReminderPlugin` 提醒无 UI 通知**：Elapsed 回调仅 `Debug.WriteLine`，用户不可见
4. **MCP 依赖路径常量不一致**：`McpDependencyService.DefaultBinDirectory`（`runtime/bin`）vs `McpServerStore.FixExecutablePathsIfNeeded` 硬编码 `%AppData%\Aemeath\tools\bin`
5. **`McpChatPlugin.SetupBuiltinMcpServers` 写旧路径**：仍写 `mcp_servers.json`，而 `McpServerStore` 已迁移到 `mcp/servers/` 单文件
6. **流式分支不调用 `FormatAemiResponse`**：流式输出会暴露原始 `<think>` 块（**注：当前代码已用 `StreamingThinkCleaner` 增量清洗，部分缓解**）

### 轻微（6 项）

7. `BoolToVisibilityConverter` 命名与实现不符且为孤立占位类
8. `Directory.Build.props` 中 `AvaloniaVersion` 变量声明但未使用（值 11.2.1 与实际 12.0.4 不符）
9. `NormalizeBaseUrl` 在三处重复实现（**注：当前已抽取到 `OpenAIUrlHelper`**）
10. `OpenAIKernelMixin.cs` 注释使用 Unicode 转义形式（**注：当前文件已为中文明文**）
11. README 下载路径与代码实际路径不一致（`%AppData%\Aemeath\tools\bin` vs `runtime/bin`）
12. `app.manifest` 要求管理员权限但 README 未说明（**注：当前 README 已补充"运行权限说明"章节**）

> **注意**：部分问题在最新代码中已修复（如 9、10、12），评审报告可能基于较早版本。修改前请先核实当前代码状态。

---

## 十四、编码与设计约定

### 14.1 命名

- 命名空间：`Aemeath.<Project>.<Module>`（如 `Aemeath.Core.AI`、`Aemeath.Desktop.Views`）
- 类名 PascalCase，私有字段 `_camelCase`
- 中文注释与中文标识符（如 `BubbleHost`、`PickLine`）混用，源码以中文注释为主

### 14.2 异步与线程

- 所有 I/O 与外部调用走 `async/await`
- UI 操作通过 `Dispatcher.UIThread.Post` 切回 UI 线程
- `AemiChatService.SetUiThreadInvoker` 让 Core 层不直接依赖 Avalonia，由 Desktop 注入
- 高风险确认闭包**永远在后台线程**执行（`Task.Run`），不阻塞 UI

### 14.3 错误处理

- Core 层异常优先 `Debug.WriteLine` + 返回错误字符串，不向 UI 抛
- `AppDomain.CurrentDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` 全局兜底
- `AppLogger` 记录到 `<exe>\log\yyyyMMdd.log`

### 14.4 资源释放

- `IAsyncDisposable` 模式（`AemiChatService` / `McpRuntimeService` / `Mem0Client` / `MemoryOrchestrator`）
- `OpenAIKernelMixin` 持有 `HttpClient` 并在重新初始化前 `Dispose`（避免 socket 累积，RES-001）
- `App.ExitApplication` 释放 ChatService（含 MCP 子进程）后再 `Shutdown`（RES-002）

### 14.5 路径策略

- **占空间大的依赖**（Python venv、向量库、UFO 源码）放在应用运行目录（`AppContext.BaseDirectory`）下的 `runtime/`，避免占用 C 盘
- **用户配置**（settings/sessions/skills）放在 `%AppData%\Aemeath`，升级时不丢、符合 Windows 规范
- 便携版/本地解压：`runtime/` 跟随程序走；正式安装到 Program Files 时若只读则回退到 `%AppData%\Aemeath\runtime`

### 14.6 第三方集成原则

- **不修改第三方核心逻辑**：Mem0 通过 Python 桥接调用其 SDK，UFO 通过子进程调用
- **C# 重新实现优先**：Hermes-Agent 的 VisionPlugin、UFO 的 ReAct 逻辑均以 C# 重新实现，仅参考思路
- **授权文件统一保存在 `assets/notices/`**

---

## 十五、扩展指南

### 15.1 新增内置 MCP 服务

1. 在 `McpBuiltinRegistry.ProtectedIds` 登记 Id（如需受保护）
2. 通过 `McpServerStore.SaveServer` 写入配置文件到 `%AppData%\Aemeath\mcp\servers\<id>.json`
3. 受保护服务会强制加载、不可删除/禁用

### 15.2 新增内置 Skill

1. 在 `Skills/<Name>/` 创建 `SKILL.md` + 人格/知识 .md 文件
2. 在 `Aemeath.Core.csproj` 添加 `EmbeddedResource`，`LogicalName` 为 `Aemeath.Skills.<name>.<file>.md`
3. 在 `SkillLoader.BuiltinSkillNames` 登记 skill 名

### 15.3 新增 KernelFunction 工具

1. 创建插件类，方法标注 `[KernelFunction("name")]` + `[Description]`
2. 在 `AemiChatService.RegisterTools` 调用 `TryRegisterPlugin(new YourPlugin(...), "name")`
3. 涉及高风险操作时注入 `ToolConfirmationService` 并调 `RequestConfirmation`

### 15.4 新增 AI Provider

`OpenAIKernelMixin` 已支持所有 OpenAI 兼容端点。新增非兼容 Provider 时：
1. 继承 `KernelMixinBase`
2. 实现 `InitializeAsync` + `BuildKernel`
3. 在 `AemiChatService.InitializeFromSettings` 添加分支

---

## 十六、文档维护

本文件由 AI 基于源码静态分析生成，反映截至 2026-07-04 的项目状态。修改代码后请同步更新本文档，特别注意：

- 新增/删除项目或重大模块时更新「解决方案架构」与「模块详解」
- 新增 NuGet 包时更新「依赖关系」
- 修复评审报告中的问题时更新「已知问题」章节并标注修复状态
- 路径约定变化时同步「关键路径约定」与 README

参考文档：
- [README.md](README.md) — 用户向项目介绍
- [项目评审报告.txt](项目评审报告.txt) — 已知问题清单
- [assets/notices/THIRD_PARTY_NOTICES.md](assets/notices/THIRD_PARTY_NOTICES.md) — 第三方致谢
