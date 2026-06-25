# 第三方资源致谢

## aemeath-mini-codex-pet

- 来源：https://github.com/cuNuo/aemeath-mini-codex-pet
- 授权：MIT
- 用途：为 Aemeath 桌宠提供额外动画 GIF 资源。
- 本仓库引用的文件：
  - `aemeath-mini-failed.gif`
  - `aemeath-mini-jumping.gif`
  - `aemeath-mini-review.gif`
  - `aemeath-mini-running-left.gif`
  - `aemeath-mini-waiting.gif`
  - `aemeath-mini-waving.gif`
- 原始授权与声明文件保留为 `aemeath-mini-codex-pet-LICENSE.txt` 和 `aemeath-mini-codex-pet-NOTICE.md`。

## ameath-ui-reference

- 来源：https://gitee.com/lzy-buaa-jdi/ameath
- 授权：MIT
- 用途：为本项目的桌宠、聊天和设置界面提供视觉风格参考，以及 Zpix 字体资源的来源说明。
- 本仓库保留的文件：
  - `assets/fonts/zpix.ttf`
  - `assets/notices/ameath-reference-LICENSE.txt`
- 说明：本项目仅参考其 UI 风格与字体气质，不直接迁移其 GIF、音效或音乐资源。

## Aemeath-skill

- 来源：https://github.com/Raindmore/Aemeath-skill
- 授权：MIT
- 用途：为本项目的桌宠聊天提供爱弥斯的语气风格和部分知识库。

## Mem0（记忆引擎）

- 来源：https://github.com/mem0ai/mem0
- 授权：Apache License 2.0
- 用途：作为 Aemeath 的核心长期记忆系统。Aemeath 通过一个自带的 Python 桥接进程调用 Mem0 的 SDK（`pip install mem0ai`），用于自动抽取、检索对话记忆。Aemeath 未修改 Mem0 核心逻辑。
- 本仓库保留的声明文件：`assets/notices/mem0-LICENSE.txt`

## Hermes-Agent（图片识别工具参考）

- 来源：https://github.com/NousResearch/hermes-agent
- 授权：MIT
- 用途：为本项目的 `VisionPlugin`（让纯文本模型具备图片识别能力）提供实现思路与提示词工程参考。Aemeath 以 C# 重新实现了其 `vision_analyze` 工具的核心逻辑（调用 OpenAI 兼容视觉模型、`image_url` + base64 协议），未直接引入其源码。
- 本仓库保留的声明文件：`assets/notices/hermes-agent-LICENSE.txt`

## UFO（电脑控制能力参考）

- 来源：https://github.com/microsoft/UFO
- 授权：MIT
- 用途：为本项目的电脑控制能力提供 Agent 规划与 UIA 操作逻辑的参考（ReAct 循环、控件标注、动作集）。Aemeath 以 C# 基于原生 UIAutomation 重新实现其规划思路；同时支持用户可选安装 UFO 作为高阶控制后端。
- 本仓库保留的声明文件：`assets/notices/UFO-LICENSE.txt`、`assets/notices/UFO-DISCLAIMER.md`