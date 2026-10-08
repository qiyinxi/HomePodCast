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

测试设备：HomePod 第二代，HomePod 软件 27.0（24J361）。

## 功能

- AirPlay 2 实时音频：瞬时配对、全程加密，延迟 100–500 ms 可调（1 ms 粒度）
- 单窗口界面：首页、混音器、麦克风与音效、设置；跟随 Windows 浅色/深色
- 场景模式：推荐（120 ms，默认）、游戏（105 ms）、音乐（500 ms，最稳）、影视（200 ms，配合浏览器插件和播放器的音频延迟设置）、自定义
- 托盘程序：自动发现音箱、自动重连、睡眠唤醒恢复、被其他设备抢占时不抢回
- 混音器：每个程序单独的音量、静音和电平表，可选按程序分流（HomePod / 本机 / 两者），见下文
- 音量上限、夜间模式（压缩动态范围 + 减弱低音）、全局快捷键；Windows 静音时键盘音量键直接调 HomePod
- 麦克风送到 HomePod，带混响和均衡，可用耳机低延迟监听；推到音箱的所有声音可选均衡预设
- 立体声对和多房间同步（实验性，尚未在真实设备上验证）
- 音画同步测试：屏幕闪光 + 「咔」声，直接看出声音比画面晚多少
- 浏览器插件（测试版）：把 YouTube、bilibili 的画面延后，和 HomePod 的声音对齐
- 界面支持英文、简体中文、繁体中文和日文

## 使用

1. 安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。
2. 运行 `HomePodCast.exe`，第一次会请求添加防火墙规则（只放行局域网、只在专用网络下生效）：HomePod 需要连回电脑对时和请求重传。
3. 程序会自动找到 HomePod 并连接。把电脑的默认输出设备静音也没关系：采集发生在系统音量之前，HomePod 照样有声。

界面语言跟随 Windows 显示语言（Windows 不是中文或日文时显示英文）。想换成别的语言：在「设置」页选择「语言 / Language」（或右键托盘图标 →「语言 / Language」），或者把 `%APPDATA%\HomePodCast\config.json` 里的 `"Language"` 设为 `"zh-CN"`、`"zh-TW"`、`"en"`、`"ja"` 或 `"auto"`。

命令行（排查问题用）：

```
HomePodCast.exe scan                         列出局域网里的 AirPlay 音箱
HomePodCast.exe stream --host <IP> --latency 120 --seconds 30
HomePodCast.exe mutetest                     确认静音后仍能采集
```

## 全部推送与按程序分流

默认情况下，HomePodCast 抓取电脑的整个声音输出，全部送到 HomePod。这条路本身不增加延迟，打游戏就用它。电脑自己的扬声器出不出声由 Windows 音量决定：把 Windows 设为静音，就只在 HomePod 播放。

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

## 构建

需要 .NET 10 SDK。

```
dotnet build src -c Release          # 输出到 app\
dotnet test tests\HomePodCast.Tests  # 协议、音频与翻译单元测试
```

`tools\measure_av.py` 可以从手机录像里算出音画差；`tests\vectors\gen_vectors.py` 用 pyatv 所用的 srptools 生成配对算法的对照数据。

界面文字用简体中文写在 `L.T("…")` / `L.F("…{0}", x)` 里，译文放在 `src/i18n/{en,zh-TW,ja}.json`，缺了哪条单元测试就会失败。浏览器插件的文字在 `extension/_locales`。

## 分支与发布

| 分支 / 标签 | 用途 | 规则 |
| --- | --- | --- |
| `dev` | 日常开发 | 每次推送都会跑 CI 并产出预览包 |
| `main` | 在真实 HomePod 上验证过的代码 | 只接受来自 `dev` 的 PR，CI 必须通过 |
| `vX.Y.Z` 标签 | 正式版本 | 打在 `main` 上，自动构建并发布 GitHub Release |
| `release/x.y` | 给旧版本打补丁 | 需要时才从对应标签拉出 |

CI 无法连接 HomePod，所以 `dev` 合并到 `main` 之前要在真机上做一次音画同步测试。

## 代码签名政策

发布的 Windows 程序计划使用 [SignPath.io](https://about.signpath.io/) 提供的免费开源代码签名，证书由 [SignPath Foundation](https://signpath.org/) 签发。

- 只签署由本仓库公开源码、经 GitHub Actions 自动构建出来的文件。
- 提交者、审核者与批准者：仓库维护者（[@qiyinxi](https://github.com/qiyinxi)）。

以英文原文为准：[Code signing policy](README.md#code-signing-policy)。

## 隐私政策

除非用户明确要求，HomePodCast 不会向其他联网系统传输任何信息：它只和你在局域网里选中的 AirPlay 音箱通信，不收集、不上传任何数据；本机状态接口只监听 127.0.0.1。以英文原文为准：[Privacy policy](README.md#privacy-policy)。

## 致谢

- [pyatv](https://github.com/postlund/pyatv)（MIT）：协议流程参考，配对算法以其依赖 srptools 为对照。
- [NAudio](https://github.com/naudio/NAudio)（MIT）：核对 Windows Core Audio 接口定义。
- [AirFlash](https://github.com/Ding-Kyoma/AirFlash)（GPL-3.0）：低延迟发包与缓冲的思路。

## 许可证

[GPL-3.0-or-later](LICENSE)。可以自由使用、修改和再发布；再发布修改版时必须同样以 GPL 开源。
