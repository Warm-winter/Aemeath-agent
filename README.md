# Aemeath Agent

一个写给鸣潮玩家的「爱弥斯」主题 Windows AI 桌宠助手。

如果你喜欢鸣潮，也喜欢让喜欢的角色真正待在桌面上，Aemeath Agent 想做的就是这件事：让“小爱”不只是壁纸或头像，而是一个能陪你聊天、记住偏好、帮你查设定、帮你打开应用、在桌面边上安静陪伴的小伙伴。

她可以待机、跟随、回应点击，也可以在你需要时打开聊天窗口。你可以和她聊日常、聊鸣潮、聊爱弥斯，也可以让她帮你处理一些电脑上的小事。涉及删除、覆盖、清空这类高风险操作时，她会先问清楚再执行。

## 这个项目适合谁

- 喜欢鸣潮、爱弥斯，希望桌面上有一个更有角色感的小助手的玩家
- 想把 AI 聊天、桌宠、语音、长期记忆和工具能力结合起来的用户
- 想研究 Avalonia + .NET 桌面 AI 应用的开发者
- 想要一个可本地配置、可控、不会把记忆和设置随便交出去的桌面助手的人

## 她能做什么

### 陪在桌面上

小爱会以桌宠的形式停留在桌面上。她支持：

- 透明置顶显示
- 鼠标拖拽移动
- 双击打开聊天
- 跟随鼠标
- 边缘吸附
- 收纳到系统托盘
- 点击反馈动画
- 气泡台词与闲置问候
- 大小与透明度调整

目前桌宠动画使用三种状态资源：待机、移动、点击。项目没有把桌宠做成复杂游戏，而是优先让她成为一个轻量、稳定、不会打扰你的桌面陪伴。

### 和你聊天

聊天窗口围绕“小爱”的日常陪伴体验设计，支持：

- 多会话管理
- 新建、删除、切换会话
- 消息复制、删除、重新回答
- Enter 发送，Shift + Enter 换行
- 聊天背景图
- 语音模式与长按录音
- Provider / Model 快速切换

项目会尽量隐藏内部工具编号、命令细节、可执行文件名等技术痕迹，让回复更像角色在和你说话，而不是系统日志。

### 更懂鸣潮与爱弥斯

项目内置了本地静态知识库，用来减少角色相关回答的胡编乱造。它覆盖爱弥斯身份、背景、性格、外貌、星炬学院、电子幽灵等基础资料，也包含一部分鸣潮相关设定。

当你问到鸣潮世界观、爱弥斯背景、角色设定、剧情事实时，小爱会优先参考本地知识库；如果问题问得比较隐晦，她也会尝试静默检索。资料没有覆盖的地方，她应该告诉你资料不足，而不是编一个听起来像真的答案。

### 记住重要的事

Aemeath Agent 带有长期记忆系统。她会在对话进行一段时间后自动总结上下文，把重要信息写入本地长期记忆。

记忆管理页支持：

- 查看长期记忆
- 编辑单条记忆
- 删除记忆
- 清空当前会话记忆
- 清空全部记忆

记忆保存在本机，用户可以随时管理。小爱可以更连续地陪你聊下去，也不会把“记住了什么”藏起来。

### 帮你做一些电脑上的小事

项目接入了 Semantic Kernel 工具能力，当前包含：

- 文件相关工具
- 浏览器与应用打开
- 截图
- 提醒
- 本地知识库检索
- MCP 插件能力

当你说“打开腾讯视频”“打开网易云音乐”这类请求时，小爱会优先尝试打开本机应用；如果找不到，再打开网页或搜索页。

涉及删除文件、覆盖已有文件、清空内容等高风险操作时，聊天中会出现确认卡。只有你点确认后才会执行。

### 支持多个 AI 服务商

设置中心提供 Provider 管理，适合使用不同 OpenAI-compatible 服务的用户。

你可以：

- 保存多个 Provider
- 配置 Endpoint、API Key、默认模型
- 获取 `/models` 模型列表
- 手动添加模型
- 测试连接
- 在聊天页快速切换 Provider 和 Model

这让你可以根据速度、价格、模型效果在不同服务之间切换，不需要反复复制粘贴配置。

### 语音输入与唤醒

项目包含语音输入与唤醒相关能力：

- 长按录音
- Windows 语音能力
- Whisper.net 转写
- Porcupine 唤醒词
- “小爱小爱”唤醒资源

语音能力仍需要结合本机环境、麦克风权限和相关 AccessKey 配置使用。

## MCP 依赖下载

项目过去内置过 `uv.exe` 和 `bun.exe`，但这些运行时二进制体积较大，不适合放进 Git 仓库。

现在的做法是：

- 仓库不提交 `/bin/`、`publish/`、项目 `bin/obj`
- 源码没有这些二进制也能构建
- 用户在 **设置中心 -> MCP 配置** 中点击 **检测/下载 MCP 依赖**
- 程序会检查本地是否已有 `uv.exe` 和 `bun.exe`
- 如果已有，会提示无需下载
- 如果缺失，会从多个国内镜像源自动降级下载

下载后的文件会保存到：

```text
%AppData%\Aemeath\tools\bin
```

下载成功后，程序会保存路径并重新配置内置 MCP Servers。

## 技术栈

- .NET 8
- C#
- Avalonia UI
- CommunityToolkit.Mvvm
- Microsoft Semantic Kernel
- NAudio
- Whisper.net
- Porcupine
- SixLabors.ImageSharp

## 仓库结构

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
│  ├─ Aemeath.Core/      # AI、设置、知识库、MCP、工具插件
│  ├─ Aemeath.Desktop/   # Avalonia 主程序、聊天窗口、设置窗口
│  ├─ Aemeath.Pet/       # 桌宠窗口、动画、跟随与交互
│  └─ Aemeath.Speech/    # 录音、语音识别、唤醒词
└─ tools/
   └─ installer.iss
```

## 构建环境

- Windows 10/11 x64
- .NET SDK 8.0 或更高版本
- 可选：Inno Setup，用于制作安装包

在仓库根目录执行：

```powershell
dotnet restore Aemeath.sln
dotnet build Aemeath.sln -c Debug
```

发布自包含版本：

```powershell
dotnet publish src/Aemeath.Desktop/Aemeath.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/Aemeath.Desktop
```

也可以使用项目内脚本：

```bat
build.bat
```

发布后运行：

```text
publish\Aemeath.Desktop\Aemeath-agent.exe
```

## 首次使用建议

1. 打开设置中心，进入 **提供商配置**。
2. 新增或编辑一个 Provider，填写 Endpoint、API Key 和默认模型。
3. 点击 **测试连接** 或 **获取模型**，确认配置是否可用。
4. 进入 **MCP 配置**，按需点击 **检测/下载 MCP 依赖**。
5. 在 **界面与行为** 中调整桌宠大小、透明度、气泡台词、边缘吸附。
6. 设置头像、聊天背景和语音唤醒。
7. 回到桌面，双击小爱开始聊天。

## 隐私与本地数据

Aemeath Agent 的用户配置遵循 `%AppData%/Aemeath` 约定。API Key 在应用内由 Windows DPAPI 按当前用户进行本地保护。

仓库已排除以下内容：

- 本地设置与环境变量文件
- API Key、Token、私密配置
- 长期记忆 JSON
- MCP 本地配置与记忆文件
- 日志、缓存、dump
- 构建产物、发布目录、安装包输出
- 大体积运行时二进制

请不要把真实 API Key、私人对话记录、长期记忆文件或本机发布产物提交到仓库。

## 验证命令

建议在提交前执行：

```powershell
dotnet build Aemeath.sln -c Debug
dotnet test Aemeath.sln
```

当前解决方案暂未包含独立测试项目，`dotnet test Aemeath.sln` 主要作为后续测试项目接入后的基线命令。

## 项目状态

项目仍在持续开发中。当前已经从“能运行的桌宠基线”推进到“面向鸣潮玩家的角色化 AI 桌面助手”。后续仍适合继续完善自动化测试、安装包发布、更多桌宠互动、更多角色知识库内容和更完整的 MCP 工具体验。

## License

本项目使用 MIT License。详见 [LICENSE](LICENSE)。
