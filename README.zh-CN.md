# Kontur Code

**请先阅读：** Kontur Code是一个**能写入你的文件、并在你的机器上运行程序的开发工具。** 这就是产品本身，而不是缺陷。下面关于智能体隔离范围的一切——以及所有已知缺口——都写在
[SECURITY.md](SECURITY.md)里。在你把它指向任何你在意的东西之前，请先读那份文档。

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

一个桌面端LLM客户端，长成了一个**空间化的AI开发环境**。一个窗口、你自己的API密钥、留在本地SQLite文件里的对话——还有一个工作区，把你指给它的文件夹变成一张真正看得见的图。

窗口之下的每一层都由两个宿主共享：一套**WPF应用程序**，以及同一套.NET内核之上的**Electron + React**外壳。因此这个应用不会被任何一套工具链的天花板所限制。

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="空间画布：工作区呈现为一张带标注边的依赖图谱" width="100%">
</p>

---

## 目录

- [截图](#截图)
- [功能](#功能)
- [智能体](#智能体)
- [隐私要点](#隐私要点)
- [系统要求](#系统要求)
- [安装](#安装)
- [首次运行](#首次运行)
- [架构](#架构)
- [开发](#开发)
- [文档](#文档)
- [参与贡献](#参与贡献)
- [许可证](#许可证)
- [状态](#状态)

---

## 截图

### 对话

<p align="center">
  <img src="docs/screenshots/chat.png" alt="一段包含助手回复和工作区上下文面板的对话" width="100%">
</p>

令牌到达时就显示出来。在回答中途停下，**已生成的那部分文本会被保留**而不是丢弃——下一轮仍可以把它当作上下文使用。

### 空间画布

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="无限画布上的节点与带标注的依赖边，以及一张缩略图" width="100%">
</p>

你的项目就是一张图谱：文件、文件夹、模块、服务、接口、数据、测试和计划都是节点，它们之间有包含边和依赖边。可以平移、缩放、框选，并在角落里查看整张图的缩略图。边都有标注——`Login() → CreateTokenAsync`是一条调用边；“仅编译期”则是一条永不运行的依赖。

### 图谱大纲

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="按节点种类分组、可筛选的图谱大纲树" width="100%">
</p>

同一张图谱，换成一份可以阅读、并按名称或路径筛选的结构。

### 编辑器

<p align="center">
  <img src="docs/screenshots/editor.png" alt="编辑器中打开的C#文件，带语法高亮和改动计数" width="100%">
</p>

CodeMirror 6，支持十种语言语法，对选中内容就地做AI修改，并提供幽灵文本补全。

### 设置

<p align="center">
  <img src="docs/screenshots/settings.png" alt="设置：主题、语言、界面与对话默认值" width="100%">
</p>

主题、界面语言、应用缩放、系统提示词、采样参数——全部本地，全部保存在你自己的数据库里。

---

## 功能

- **流式对话。** 令牌边生成边到达。停止会保留部分回答。重新生成会就地替换，也可以换用另一个模型。
- **三种工作模式。** *Chat*用于对话，*Cowork*用于分析，*Code*则让智能体拿到工作区和工具循环。模式是消息的属性，而不是应用的属性，所以“先规划，再动手”是两条消息，而不是两次跑去设置。
- **空间图谱。** 你的文件夹会被自动索引成节点和边。基于差异的索引器会加入新文件、去掉已删除的文件，并且**保留你排好的布局**。智能体产出的计划会作为节点和边的集合落到画布上——可以撤销、会被保存，也由你来否决。
- **统一的工作区界面。** 作为地图的画布、作为结构的图谱、文件树、编辑器、git面板、一次运行的轨迹以及任务视图——彼此之间只隔着一次`Ctrl+Shift+P`。
- **Git。** 状态、已暂存与未暂存的差异、暂存、提交、切换分支、回退、推送、拉取、抓取。全部通过`git`完成，**不使用shell**，参数也经过校验。
- **令牌统计。** 实时用量、预估成本，以及模型此刻到底在上下文里存着什么——还有一个**压缩会话**按钮，把较早的轮次折叠成一段摘要。
- **Markdown渲染。** 标题、列表、表格、引用、任务列表和围栏代码块，带语法高亮。渲染为结构化内容，**绝不作为注入的HTML**。
- **模型目录。** 从各家提供方拉取并缓存在SQLite中，因此之后选择器离线也能用。上下文窗口、价格和能力来自提供方，而不是一份写死的清单。
- **开箱即用的两家提供方**——OpenRouter和NVIDIA NIM，都兼容OpenAI。把NVIDIA的端点指向本地Ollama、LM Studio或自建的NIM容器，就不会有任何东西离开你的机器。
- **会话打包。** 把整个会话——对话、画布、文件、目标——导出成一个`.zip`。
- **三种语言。** 英语、俄语和德语，实时应用到整个界面。
- **浅色与深色**，跟随系统或者固定下来。

---

## 智能体

智能体运行一个工具循环，而在使用它之前，最该弄清楚的就是它的作用范围。

| | |
| --- | --- |
| **工作范围** | 你指定的某一个文件夹，并且拒绝读写它之外的任何东西 |
| **在该文件夹内同样拒绝** | `.git`、`.env`、`credentials.json`、`*.pem`、`*.key`、`*.pfx`——按名称，一律拒绝 |
| **事先询问** | 每一次写入、每一个外部文件、每一次网络请求、每一个程序 |
| **绝不做** | 运行shell。`&&`、`\|`、`>`和`$HOME`只是传给程序的文本 |
| **程序** | 默认关闭。即便开启，也只有一份由人手工编辑的允许清单。而且*每一次*调用都要批准 |
| **撤销** | 靠你的版本控制。改动是在做出之前展示，而不是做出之后回退 |

一次拒绝会指明是哪条规则，并告诉模型该改做什么，所以它不会再连着三次伸手去拿同一个工具。

**该文件夹之外的一切都是按需开启的，在你打开之前一直处于关闭状态。** 网络获取和项目外文件访问在设置里是两个彼此独立的开关，而且每一次调用依然要经过批准提示。

> 完整的隔离模型——以及**八项已知缺口**，其中一项可以让Windows的junction绕过
> 针对项目外文件的凭据文件名规则——写在
> [SECURITY.md](SECURITY.md)里。这是个alpha；在信任它之前请先读一读。

---

## 隐私要点

- **没有遥测。没有分析。没有崩溃上报。没有账号。** 这个仓库里没有任何代码会去连接本项目拥有的任何地址。
- **你的对话不会碰到任何服务器。** 它们是你自己用户配置文件里的一个SQLite文件。
- **API密钥经过加密**，使用Windows DPAPI，限定在你的Windows账户内，也绝不写进日志。
- **离开你机器的东西：** 只有你发给模型提供方的内容，而且只在你按下发送时才会离开。网络目标的完整清单在
  [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it)中。
- **连续的多轮记录不用这个应用也能读。** 数据库在落盘时并未加密——这是一个有意为之的取舍，写在文档里而不是含糊带过。
- **你的模型提供方能看到你的提示词**，适用的是*它*的政策，而不是本项目的政策。这就是一个为别人的模型而做的客户端所接受的条款。

按照GDPR、俄罗斯152-FZ和CCPA/CPRA写就的完整细节在
[PRIVACY.md](PRIVACY.md)里。那里也说明了如何导出，以及如何全部删除。

---

## 系统要求

- Windows 10版本1809或更高，或Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download)——仅安装包需要；已发布的构建需要运行时，源码则需要SDK
- 来自[OpenRouter](https://openrouter.ai)或[NVIDIA](https://integrate.api.nvidia.com)的API密钥
- 约500MB磁盘空间，以及一个你愿意让智能体读取的文件夹

没有跨平台构建。DPAPI和WPF仅限Windows，目标框架把这一点直接写在明处，而不是等到运行时才失败。

---

## 安装

从[发布页](https://github.com/rwarx/kontur-code/releases)下载安装包。这是按用户安装的NSIS安装——不需要管理员权限。

第一个版本是**alpha**。发布它是因为形态已经稳定到可以据此开发，而不是因为它已经可以无人值守地使用。

<details>
<summary>自己动手构建</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# sidecar必须发布到Electron会去找它的那个位置
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

只构建.NET解决方案会得到WPF宿主：

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## 首次运行

1. 在**设置→提供方**里粘贴一个API密钥，然后点**刷新**。在有一家提供方成功之前，模型选择器一直是空的——目录之后会被缓存起来，所以从那时起离线也能用。
2. **打开一个文件夹。** 在*Code*模式下，把它指向一个项目。它会被索引进图谱，从那一刻起，智能体的世界就是那个文件夹。
3. **在放手让它工作之前先提交。** 你愿意的话，`git commit`一个空提交即可。智能体会直接写进你的工作树，既没有任何暂存内容，也没有任何备份；你的历史就是撤销手段，而且是唯一的手段。
4. 如果你打算启用命令执行或项目外文件访问，请**阅读[SECURITY.md](SECURITY.md)**。两者默认都是关闭的，而且两者都是边角最锋利的功能。

---

## 架构

五个项目，一条规则：**依赖一律向内。** `Domain`和`Application`以纯`net10.0`为目标，因此去碰WPF或DPAPI会变成编译错误，而不是一条评审意见。

```text
AIClient.Domain ◄──── AIClient.Application ◄──── AIClient.Infrastructure
                          ▲                          ▲            ▲
                          └──────── AIClient.App ────┘            │
                          └──────── AIClient.Server ─────────────┘
```

```text
provider bytes ──► AIStreamEvent ──► ChatTurnEvent ──► the UI
   (SSE frames)       (Domain)          (Application)    (WPF or React)
```

三套事件词汇，一套比一套更窄，并在每个边界上做翻译。提供方无法把数据库id放进它返回的类型里，因为它返回的类型并不是界面消费的类型。

本地API每次启动都要求一个bearer token，并且拒绝绑定到loopback以外的任何地址——处在`127.0.0.1`并不是一道授权边界，而代码也并未把它当成边界。

包括通往文件系统的两扇门和两个画布渲染器在内的完整推导，见
[ARCHITECTURE.md](ARCHITECTURE.md)。

---

## 开发

```bash
dotnet build AIClient.slnx     # 警告即错误——这是刻意的
dotnet test                    # 896个测试，不需要网络，也不需要API密钥

cd electron
npm install
npm run typecheck
npm run dev                    # 针对预置的演示工作区运行渲染进程，无需后端
```

需要Windows和.NET 10 SDK。Node 22只在渲染进程时需要。

真正要紧、而`.editorconfig`无法表达的约定写在
[CONTRIBUTING.md](CONTRIBUTING.md)里。

---

## 文档

| 文档 | 内容 |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 代码为什么是这个形状。改动结构之前请先读。 |
| [DEVELOPMENT.md](DEVELOPMENT.md) | 构建、迁移、测试、扩展。改动任何东西之前请先读。 |
| [SECURITY.md](SECURITY.md) | 威胁模型、哪些东西受到保护，**以及已知缺口**。 |
| [PRIVACY.md](PRIVACY.md) | 存在哪些数据、它们去了哪里，以及你的权利。GDPR / 152-ФЗ / CCPA。 |
| [CHANGELOG.md](CHANGELOG.md) | 每一次改动，安全修复都特别标出。 |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | 随附的组件及其许可证。 |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | 如何参与。 |

---

## 参与贡献

欢迎贡献。对智能体安全模型改动的评审门槛之所以定得高，是有意为之——因为那份代码能在你的机器上写文件、运行程序。

请从[CONTRIBUTING.md](CONTRIBUTING.md)开始。简短地说：一个拉取请求只做一处逻辑改动，`dotnet test`全绿；如果你动了智能体的作用范围，就在描述里说明你把它放在了哪一道闸门之后。

请**不要为安全漏洞创建公开issue**——私下报告的方式见
[SECURITY.md](SECURITY.md)。

---

## 许可证

**MIT。** 见[LICENSE](LICENSE)。

第三方组件保留各自的许可证——约40个随附包，再加上Electron和Chromium——都记录在[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)中。

---

## 状态

`0.1.0-alpha`。刻意以预发布的形式发布。

**可用的：** 流式对话、两个宿主、空间图谱与画布、带审批闸门的智能体工具循环、编辑器、git、会话与打包、三种语言。

**已知不好用的部分**——全部列在[CHANGELOG](CHANGELOG.md#known-limitations)里，也全部连同文件引用列在[SECURITY.md](SECURITY.md#known-gaps)里：

1. 一个Windows junction可以绕过针对项目外文件访问的凭据文件名规则。
2. 两个批准同时到达时，可能让一次运行挂住，而不是让它失败。
3. 事件流没有心跳，也没有重连——连接一断，这次运行就没了。
4. 编辑器每敲一个键就写一次磁盘，没有任何防抖。
5. 渲染进程会把工作区文件内容持久化到`localStorage`里；大型项目可能超出浏览器配额。
6. 最新的那些界面——外部文件工具、获取器、服务器、git操作——没有测试覆盖。本次发布所做的修复*是有覆盖的*。

这是一个来自小型项目、背后没有任何资金的`0.x`版本。它在公开场合开发，issue尽力回答，也没有SLA。如果你需要SLA，那是一场和供应商之间的对话，而不是和这个仓库之间的对话。

---

<p align="center"><sub>MIT许可。公开开发。截图取自运行中的应用。</sub></p>
