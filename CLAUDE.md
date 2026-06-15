# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

Aemeath Agent 是一个基于 .NET 8 + Avalonia 的 Windows AI 桌宠助手，面向《鸣潮》游戏玩家，以"爱弥斯"角色为主题。项目结合了桌宠交互、AI 对话、本地知识库、长期记忆、语音识别和 MCP 工具能力。

## 常用命令

### 构建与运行

```powershell
# 恢复依赖
dotnet restore Aemeath.sln

# 调试构建
dotnet build Aemeath.sln -c Debug

# 发布版本构建
dotnet build Aemeath.sln -c Release

# 发布自包含可执行文件
dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop

# 或使用项目脚本（包含完整流程）
build.bat
```

### 运行程序

```powershell
# 调试运行
dotnet run --project src/Aemeath.Desktop/Aemeath.Desktop.csproj

# 发布后运行
publish\Aemeath.Desktop\Aemeath-agent.exe
```

## 架构设计

### 项目结构

- **Aemeath.Core**：AI 核心逻辑、配置管理、知识库、MCP 集成、工具插件系统
- **Aemeath.Desktop**：Avalonia 主应用、聊天窗口、设置中心、MCP 配置界面
- **Aemeath.Pet**：桌宠窗口、GIF 动画播放、跟随与交互逻辑
- **Aemeath.Speech**：语音录音、Windows 语音 API、Whisper.net 转写

### AI 服务架构

#### 核心抽象层

- **`KernelMixinBase`**：所有 AI 服务的基类，封装 Semantic Kernel 的初始化、消息发送（流式和非流式）、附件处理（文本、图片）、插件注册。所有具体实现继承此类。

- **`AemiChatService`**：主聊天服务实现，整合所有功能：
  - Provider 管理与动态切换
  - 本地知识库自动注入
  - 工具插件注册（文件系统、浏览器、截图、提醒、知识库、MCP）
  - 响应格式化（移除 `<think>` 等内部推理标签）
  - 长期记忆总结（使用独立 Kernel 实例）

#### Provider 支持

项目通过 OpenAI-compatible 接口支持多个 LLM 服务商。关键实现：

- **`OpenAIKernelMixin`**：基于 `Microsoft.SemanticKernel.Connectors.OpenAI`，支持自定义 Endpoint
- **`AnthropicKernelMixin`**（如存在）：Anthropic 专用适配
- **`OpenAIResponseNormalizationHandler`**：处理非标准响应格式，确保兼容性

配置存储在 `Settings.ApiKeys`（Dictionary<string, ApiKey>），支持多 Provider 并行配置。

### 本地知识库系统

- **资源文件**：`Aemeath.Core/Knowledge/knowledge_base.zh-CN.json`（EmbeddedResource）
- **检索逻辑**：`KnowledgeBaseService` 使用简单关键词匹配，在用户消息发送前自动注入相关条目
- **注入策略**：
  - 用户消息先经过 `EnrichMessageWithKnowledge` 预处理
  - 自动命中的知识库条目会添加到消息前缀
  - System Prompt 提示模型优先依据本地知识库，资料不足时明确说明而非编造

### 工具插件系统

基于 Semantic Kernel 的 Plugin 机制，所有工具通过 `[KernelFunction]` 特性暴露给 LLM：

- **`FileSystemPlugin`**：文件读写、删除、移动、目录操作（需确认）
- **`BrowserPlugin`**：打开应用、网页、搜索（优先本地应用，回退网页）
- **`ScreenshotPlugin`**：全屏或区域截图
- **`ReminderPlugin`**：定时提醒
- **`KnowledgeBasePlugin`**：供 LLM 主动调用 `knowledge_search`
- **`McpChatPlugin`**：MCP 工具代理层

#### 高风险操作确认机制

- **`ToolConfirmationService`**：追踪待确认操作（`PendingToolAction`）
- 删除、覆盖、清空等高风险操作会先返回确认请求，UI 显示确认卡，用户确认后才执行
- 工具插件通过构造函数注入 `ToolConfirmationService` 实现此机制

### MCP (Model Context Protocol) 集成

- **`McpRuntimeService`**：管理外部 MCP Servers 的生命周期
  - 从用户配置文件加载 Server 列表（默认路径或用户指定）
  - 启动 `uv` / `bun` 进程连接 MCP Server
  - 动态构建 `KernelPlugin`，将 MCP tools 转为 Semantic Kernel functions
  - 支持异步重载（后台加载，超时 130 秒）
  
- **依赖下载机制**：
  - `McpDependencyService` 负责检测和下载 `uv.exe` / `bun.exe`
  - 支持多个国内镜像源自动降级下载
  - 下载到 `%AppData%\Aemeath\tools\bin`
  - 用户通过 **设置中心 -> MCP 配置 -> 检测/下载 MCP 依赖** 触发

### 配置与持久化

- **配置存储**：`%AppData%\Aemeath\`
  - `appsettings.json`：主配置文件（Provider、模型、桌宠设置等）
  - API Key 使用 Windows DPAPI 加密（`System.Security.Cryptography.ProtectedData`）
  - MCP Servers 配置独立文件
  - 长期记忆 JSON 文件

- **`SettingsService`**：单例管理配置的加载、保存、更新

### 响应格式化

`AemiChatService.FormatAemiResponse` 清理 LLM 原始输出：
- 移除 `<think>...</think>` 块
- 移除 `/think.../endthink` 块
- 移除 ` ```think...``` ` 块
- 确保用户看到的回复更像角色对话，而非系统日志

### 长期记忆

- 对话进行一段时间后，UI 层触发总结请求
- `AemiChatService.SummarizeAsync` 使用独立的临时 Kernel 实例执行总结
- 总结结果保存到本地 JSON 文件，下次对话可加载

## 开发注意事项

### 依赖管理

- 使用 **Central Package Management**（`Directory.Packages.props`），所有 `<PackageReference>` 不带 Version
- 主要依赖：
  - Avalonia 11.2.1
  - Microsoft.SemanticKernel 1.20.0
  - ModelContextProtocol.Core 1.3.0
  - Whisper.net 1.8.1

### Git 排除规则

不要提交以下内容：
- `/bin/`, `publish/`, 各项目的 `bin/obj`
- API Key、Token、环境变量文件
- 长期记忆 JSON、日志、缓存
- MCP 配置和记忆文件
- `uv.exe`, `bun.exe` 等大体积运行时二进制

### Semantic Kernel 特性抑制

项目 `.csproj` 中包含 `<NoWarn>$(NoWarn);SKEXP0010</NoWarn>` 以抑制 Semantic Kernel 实验性 API 警告（`ToolCallBehavior`）。

### 多语言与角色化

- 所有用户可见文本使用中文
- System Prompt 设计为角色化对话（`AemiSystemPrompt.Default` / `Professional`）
- 避免在回复中暴露工具编号、命令细节、可执行文件名等技术痕迹

## 资源与资产

- **静态资源**：`assets/static/` - 图标、头像、背景图
- **桌宠动画**：`assets/animations/pet/` - 待机、移动、点击 GIF
- **第三方授权**：`assets/notices/` - 资源来源与致谢

## 测试

当前解决方案暂无独立测试项目，`dotnet test Aemeath.sln` 作为后续测试接入的基线命令。
