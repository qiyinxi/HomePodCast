# HomePodCast

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md)

把 Windows 电脑的声音实时推送到 HomePod（AirPlay 2），为打游戏和看视频做的低延迟方案。

TuneBlade 在 HomePod 软件 27 上已经无法出声；HomePodCast 是从头实现的原生 C# 版本，单个 exe 约 350 KB，不需要虚拟声卡。

## 实测

| 场景 | 数值 |
| --- | --- |
| 声音比画面晚（延迟设 120 ms，手机 60 帧录像实测） | 156 ms |
| 声音比画面晚（延迟设 105 ms，推算） | 约 141 ms |
| 推流统计（断音 / 迟发 / 重传） | 0 / 0 / 0 |
| 内存（私有） | 约 28 MB |

## 测试设备

HomePodCast 是个人项目，实际只在一套设备上测试过：

- 音箱：一台 HomePod（第二代），HomePod 软件 27.0（24J361），Wi‑Fi 连接
- 电脑：一台 Windows 11 电脑，默认输出是虚拟声卡

没有在真实设备上测过的：

- HomePod mini、HomePod（第一代）、Apple TV、其他品牌的 AirPlay 音箱
- **立体声对和多房间**：按协议资料实现，只用模拟音箱测试过
- 实体声卡和耳机、其他 Windows 版本，以及电脑走有线或无线网络的差别

如果你在别的设备上试用，欢迎开 issue 反馈：设备型号、软件版本、能稳定不断音的最低延迟设置，以及日志（`%APPDATA%\HomePodCast\homepodcast.log`）。

## 功能

- AirPlay 2 实时音频：瞬时配对、全程加密，延迟 100–500 ms 可调（1 ms 粒度）
- 单窗口界面：首页、混音器、麦克风与音效、设置；跟随 Windows 浅色/深色
- 场景模式：推荐（120 ms，默认）、游戏（105 ms）、音乐（500 ms，最稳）、影视（200 ms，配合浏览器插件和播放器的音频延迟设置）、自定义
- 托盘程序：自动发现音箱、自动重连、睡眠唤醒恢复、被其他设备抢占时不抢回
- 右键托盘图标打开快捷面板：音量和静音、场景、夜间模式、麦克风、连接或断开
- 混音器：每个程序单独的音量、静音和电平表，可选按程序分流（HomePod / 本机 / 两者）或按声卡分流（选择采集哪个输出设备），见下文
- 音量上限、夜间模式（压缩动态范围 + 减弱低音）、全局快捷键；键盘音量键也能调 HomePod（跟随 Windows 音量，或推流时只调 HomePod），见下文
- 麦克风送到 HomePod，带混响和均衡，可用耳机低延迟监听；推到音箱的所有声音可选均衡预设
- 立体声对和多房间同步（实验性，尚未在真实设备上验证）
- 音画同步测试：屏幕闪光 + 「咔」声，直接看出声音比画面晚多少
- 浏览器插件（测试版）：把 YouTube、bilibili 的画面延后，和 HomePod 的声音对齐。「音乐」场景的 500 ms 是给听音乐用的，看视频用「影视」或「推荐」最流畅（画面延迟超过 300 ms 时插件弹窗会提示）
- 界面支持英文、简体中文、繁体中文和日文

## 使用

1. 安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）。
2. 从 [Releases](https://github.com/qiyinxi/HomePodCast/releases) 下载 HomePodCast，有两种包：
   - **安装包（推荐）`HomePodCast-<版本>-x64.msi`**：可以选择安装位置（默认 `C:\Program Files\HomePodCast`），自动添加开始菜单快捷方式和所需的防火墙规则。升级时直接运行新版 MSI；卸载在「设置 → 应用」里进行，如果开启过「开机自动启动」，请先在程序里关掉。
   - **便携版 `HomePodCast-<版本>-win-x64.zip`**：解压到任意位置，运行 `HomePodCast.exe`，第一次会请求添加防火墙规则。

   防火墙规则只放行局域网、只在专用网络下生效：HomePod 需要连回电脑对时和请求重传。两种包的设置都保存在 `%APPDATA%\HomePodCast`。
3. 程序会自动找到 HomePod 并连接。采集发生在系统音量之前，Windows 音量不会改变送出去的声音；HomePod 有自己的音量。默认 Windows 音量就是 HomePod 音量：两边保持一致（连接时对齐到较小的那个），Windows 静音时 HomePod 也会静音。如果默认输出是电脑扬声器、又只想用 HomePod 听，请在「设置 → 键盘音量键」选「推流时控制 HomePod」，再把 Windows 静音（见[键盘音量键](#键盘音量键)）。

界面语言跟随 Windows 显示语言（没有对应译文的语言显示英文）。想换成别的语言：在「设置」页选择「语言 / Language」，或者把 `%APPDATA%\HomePodCast\config.json` 里的 `"Language"` 设为语言代码（`"zh-CN"`，或 `src/i18n` 里的文件名，比如 `"en"`、`"de"`）或 `"auto"`。

命令行（排查问题用）：

```
HomePodCast.exe scan                         列出局域网里的 AirPlay 音箱
HomePodCast.exe stream --host <IP> --latency 120 --seconds 30
HomePodCast.exe mutetest                     确认静音后仍能采集
```

## 全部推送与按程序分流

默认情况下，HomePodCast 抓取电脑的整个声音输出，全部送到 HomePod。这条路本身不增加延迟，打游戏就用它。电脑自己的扬声器出不出声由 Windows 音量决定。想只在 HomePod 播放：在「设置 → 键盘音量键」选「推流时控制 HomePod」（或「仅在 Windows 静音时」），再把 Windows 设为静音；默认的「HomePod 跟随 Windows 音量」下，Windows 静音会让 HomePod 也静音。

按程序分流可以让每个程序分别送到 HomePod、本机或两者（在「混音器」页设置），比如游戏声音进 HomePod、语音聊天留在耳机里。代价是延迟：

| | 全部推送（默认） | 按程序分流 |
| --- | --- | --- |
| 何时启用 | 没开分流时 | 有程序单独设了去处，或默认去处是「本机」 |
| 怎么抓声音 | 整个输出一次抓取 | 逐个程序抓取（Windows 进程环回） |
| 推到 HomePod 的额外延迟 | 无 | 约 35 ms（实测从播放到发出：21 ms 对 55 ms） |
| 网页视频音画同步（浏览器插件） | 自动补偿 | 自动补偿，已含额外的 35 ms |
| 游戏 | 延迟最低 | 多 35 ms，无法补偿 |
| 设为「HomePod」的程序 | — | 本机静音，Windows 音量合成器里显示 0% |
| 系统提示音 | 跟随 Windows 音量 | 只在本机播放 |
| 系统要求 | Windows 10/11 | Windows 10 2004 或更高版本 |

第一次开启分流时程序会弹窗确认。把所有程序改回「默认」并把默认设为 HomePod，就回到全部推送。如果分流时 HomePodCast 被强制结束，设为「HomePod」的程序会保持 0%，直到再次运行 HomePodCast。

### 按声卡分流（采集设备）

「混音器 → 采集设备」决定推送哪个输出设备上的声音，默认跟随 Windows 默认输出。选一个你不用来听的设备，再在 Windows 的应用音量设置（混音器页有按钮直达）或程序自己的设置里，把要送到 HomePod 的程序（比如游戏）的输出设为它；语音聊天等其他程序照常在耳机里播放。整个设备一次抓取，和全部推送一样没有额外延迟，不像按程序分流那样多约 35 ms。

- 需要一个空闲的或虚拟的声卡，比如 VB-CABLE、网易 UU 加速器的虚拟声卡、Steam Streaming Speakers，或不带喇叭的 HDMI 显示器。HomePodCast 没法自己装虚拟声卡：那是要微软签名的内核驱动。
- 有些游戏只在启动时选择输出设备，改完要重启游戏。
- 所选设备被拔掉、停用或卸载时，不会改用默认输出（以免把耳机里的语音推到音箱）：暂停采集并在首页提示，设备恢复后自动继续。
- 按程序分流可以叠加使用，规则只作用于这个设备上的程序。
- 指定了采集设备时，「HomePod 跟随 Windows 音量」跟随的是这块设备的音量；音量键只调 Windows 默认输出。想用音量键调 HomePod，就把这块设备设为默认输出。
- 推流时，如果麦克风「本机监听」用的正是被采集的设备（比如两者都跟随默认输出），本机监听会暂停，否则人声会被推到 HomePod 两次。

## 键盘音量键

在「设置 → 键盘音量键」里选择音量 +、− 和静音键对 HomePod 起什么作用：

- **HomePod 跟随 Windows 音量**（默认）：推流时 Windows 音量就是 HomePod 音量。Windows 的 0–100% 对应 HomePod 的 0 到音量上限；按键和 Windows 音量滑块会改 HomePod，在程序里调 HomePod 也会改 Windows。连接时和改音量上限时，两边对齐到较小的那个，哪边都不会自己变大。Windows 静音时 HomePod 也静音。
- **推流时控制 HomePod**：推流时这三个键只调 HomePod（每次 2%），Windows 音量不变，屏幕下方会显示 HomePod 音量（独占全屏的游戏里不显示）；不推流时照常调 Windows。适合默认输出是电脑扬声器、又只想听 HomePod 的情况：把 Windows 静音，再用按键调 HomePod。
- **仅在 Windows 静音时**：旧版的做法。只有 Windows 静音或音量为 0 时按键才转给 HomePod，Windows 保持静音。
- **关**：按键只调 Windows。

电脑没有可用的输出设备时（没装声卡驱动，或设备被禁用、拔掉），「HomePod 跟随 Windows 音量」和「仅在 Windows 静音时」也会在推流时直接用按键控制 HomePod，直到设备恢复。以管理员身份运行的程序在前台时，以及遥控器、鼠标的驱动软件只发送媒体命令而不是按键时，按键仍然调 Windows；这种情况下「HomePod 跟随 Windows 音量」会跟着变。

## 常见问题

### 偶尔卡一下怎么办

**为什么会卡**：声音要走两段无线，电脑 → 路由器 → HomePod。HomePod（第二代）只有 Wi‑Fi 4（802.11n），有人走过、抬一下手、墙和柜门都会挡信号，偶尔就有个包晚到或丢了。延迟设 120 ms（「推荐」）时，HomePod 自己要用约 85 ms 处理，留给网络抖动和丢包重发的只有约 35 ms，一次超过这个余量的抖动就是一下卡顿。

**按这个顺序试**：

1. 电脑能插网线就插网线：两段无线变成一段。
2. Windows 的无线网卡节能：控制面板 → 电源选项 → 更改计划设置 → 更改高级电源设置 →「无线适配器设置 → 节能模式」，使用电池和接通电源都设为「最高性能」。
3. 设备管理器 → 网络适配器 → 无线网卡 → 属性 →「高级」：「漫游主动性」（Roaming Aggressiveness）设为最低；如果有，再把「MIMO 节能模式」（MIMO Power Save Mode）设为「无 SMPS」（No SMPS），「数据包合并」（Packet Coalescing）设为关闭。名称因网卡而异。
4. HomePod 的位置：放在「看得见」路由器的地方，不要放进柜子里。
5. 把延迟加 10–15 ms：在「场景」里选「自定义」调整，或者首页出现提示时直接点「应用」。首页「推流状态」下面会显示最近 10 分钟的网络抖动次数；抖动真的吃掉余量时，提示会说明是哪一段 Wi‑Fi 的问题，并给出建议的延迟。程序不会自己改延迟。

**怎么看日志**（`%APPDATA%\HomePodCast\homepodcast.log`）：推流时每 100 ms ping 一次 HomePod 和路由器，往返超过 30 ms 或没有回应时记一行 `network: ping to the speaker took 48 ms`（路由器是 `the router`，没有回应是 `lost`）。每分钟的 `stats:` 行里有 `rtx=已重发/请求`、`rtxMiss=`（来不及重发的包）和 `ping=中位/p99/最大 lost=丢失/总数 router=…`。

- 只有音箱的 ping 突增、路由器正常：问题在路由器 → HomePod 这一段，见第 4 条。
- 音箱和路由器同时突增：问题在电脑这一段，见第 1–3 条。
- 只有路由器慢、音箱正常：路由器常把发给自己的 ping 放在最后处理，不影响声音。

## 构建

需要 .NET 10 SDK。

```
dotnet build src -c Release          # 输出到 app\
dotnet test tests\HomePodCast.Tests  # 协议、音频与翻译单元测试
```

MSI 安装包由 `installer\build.ps1` 用 WiX Toolset v5（本地 dotnet 工具）构建，详见 [installer/README.md](installer/README.md)。

`tools\measure_av.py` 可以从手机录像里算出音画差；`tests\vectors\gen_vectors.py` 用 pyatv 所用的 srptools 生成配对算法的对照数据。

界面文字用简体中文写在 `L.T("…")` / `L.F("…{0}", x)` 里，译文放在 `src/i18n/<语言>.json`（每个文件就是一种界面语言，以 `en.json` 为准），缺了哪条或和 `en.json` 对不上，单元测试就会失败。浏览器插件的文字在 `extension/_locales`。

## 分支与发布

| 分支 / 标签 | 用途 | 规则 |
| --- | --- | --- |
| `dev` | 日常开发 | 每次推送都会跑 CI 并产出预览包 |
| `main` | 在真实 HomePod 上验证过的代码 | 只接受来自 `dev` 的 PR，CI 必须通过 |
| `vX.Y.Z` 标签 | 正式版本 | 打在 `main` 上，自动构建并发布 GitHub Release |
| `release/x.y` | 给旧版本打补丁 | 需要时才从对应标签拉出 |

CI 无法连接 HomePod，所以 `dev` 合并到 `main` 之前要在真机上做一次音画同步测试。

## 代码签名

目前发布的程序还没有数字签名，第一次运行安装包或 exe 时 Windows SmartScreen 可能会拦截（点「更多信息」→「仍要运行」）。所有发布文件都由 GitHub Actions 从本仓库的公开源码自动构建，请只从 [Releases](https://github.com/qiyinxi/HomePodCast/releases) 页面下载。

## 隐私政策

除非用户明确要求，HomePodCast 不会向其他联网系统传输任何信息：它只和你在局域网里选中的 AirPlay 音箱通信，不收集、不上传任何数据；本机状态接口只监听 127.0.0.1。以英文原文为准：[Privacy policy](README.md#privacy-policy)。

## 致谢

- [pyatv](https://github.com/postlund/pyatv)（MIT）：协议流程参考，配对算法以其依赖 srptools 为对照。
- [NAudio](https://github.com/naudio/NAudio)（MIT）：核对 Windows Core Audio 接口定义。
- [AirFlash](https://github.com/Ding-Kyoma/AirFlash)（GPL-3.0）：低延迟发包与缓冲的思路。

## 许可证

[GPL-3.0-or-later](LICENSE)。可以自由使用、修改和再发布；再发布修改版时必须同样以 GPL 开源。
