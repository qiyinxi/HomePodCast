# 实机测试清单

分三部分：

- **每版回归**（紧接着这一节）：每个测试版发布以后做，几分钟就够，不卡发版。正式版是把测过的测试版原样转正，不用再测一遍。
- **一次性验证**：测过一次就打勾、写上谁哪天测的，以后只有条目后面「改了 … 才需要重测」里的代码改了才重测。发测试版时，会单独列出这一版需要重测的条目。音质、夜间模式和均衡的听感、手机录像测延迟、安装向导的样子这类，都属于这一部分。标着「实验性」的功能单独一节：实机测过才去掉标注。
- **已自动化（不用手测）**：CI 每次构建都会测的项，写明是哪个作业或测试类。

测试时 HomePod 音量不超过 25%。每一项打勾或写下现象；有问题的项写清楚“做了什么 → 看到/听到什么”。

标注的意思：「单元测试：…」是 `tests/HomePodCast.Tests` 里的测试类，用模拟的音箱、Apple TV、播放器和接口测程序自己的逻辑，每次 CI 构建都跑；「CI 自动测试：…」是 `.github/workflows/build.yml` 里的作业；「仍需实机：…」是自动测试测不到、还要人看或听的部分（声音、画面、真实设备的反应）。

## 每版回归（每个测试版发布后）

1. - [ ] 设置 →「软件更新」→「检查更新」→「更新」，在 UAC 里点「是」：新版自己重新打开，托盘提示「已更新到 x」。下载、校验、安装、重新打开这几步 CI 每次都自动测过，这里只是你平时装新版的操作；UAC 弹框本身只有人能看到。
2. - [ ] 连上 HomePod，用平时的延迟放一两分钟：听不到断音，首页没有黄色提示。
3. - [ ] 看一眼这一版 CI 存下的界面截图：在 GitHub 上打开这个测试版的标签构建（Actions → build → 这次运行），在页面底部 Artifacts 里下载 `gui-screenshots`，解压后打开 `index.html`：有没有错位、缺字、文字被截断。被省略号截断的文字和没有无障碍名称的控件，作业摘要里列为警告；崩溃、日志里的 ERROR、控件被裁掉或重叠、没翻译的中文，CI 会直接报错、构建不通过。

## 一次性验证

### 一、推流与延迟

- [x] 推流回归：120 ms / 105 ms，断音、迟发、跳包、重传都是 0（Claude 2026-10-08 实机测过）。改了 `src/Net/RtpSender.cs`、`src/Audio/AudioFifo.cs` 或 `src/StreamController.cs` 才需要重测
- [x] 建立参数实验：HomePod 报告的处理时间固定 85 ms（只有 `moviePlayback` 模式会升到 112），现有参数已是最低（Claude 2026-10-08 实机测过）。改了 `src/Net/AirPlayClient.cs` 的 SETUP 参数才需要重测
- [x] 自动连接真实的 HomePod，首页推流状态：已连接、缓冲、音箱处理 85 ms、断音 0（Claude 2026-10-08 实机测过；连假 HomePod 时的首页：CI 自动测试：gui-smoke）。改了 `src/UI/TrayApp.cs` 的自动连接才需要重测
- [x] 30 分钟稳定性（105 ms、完整采集链路、UU 虚拟声卡）：网络侧 0 跳包 0 重传；固定 12 ms 缓冲断音 25 次 → 改成自适应缓冲后 2 次（稳定在 18 ms）。之后又加了“不退回到出过断音的大小”，见「测试记录」（Claude 2026-10-08 实机测过）。改了 `src/Audio/AudioFifo.cs` 的自适应缓冲或采集（`src/Audio/LoopbackCapture.cs`）才需要重测
- 手机 60 帧录像测延迟（最重要）：诊断 → 音画同步测试 → 开始，手机离屏幕和音箱都约 50 cm，录 15 秒以上，**发原文件**（不要经微信压缩）。改了 `src/Scenes.cs`、`src/Audio/AudioFifo.cs`、`src/Net/RtpSender.cs`、`src/Net/TimingServer.cs` 或 `src/UI/SyncTestForm.cs` 才需要重测
  - [x] 场景「游戏」（105 ms）：声音比画面晚 **118 ms**（8 下，115–125；目标 ≤ 145）。手机录像本身约 ±10 ms 误差（用户 2026-10-08 录像）
  - [x] 场景「推荐」（120 ms）：**137 ms**（10 下，124–144；上次 156）。当时缓冲被误涨到 20 ms（beta.4 已修），修好后应再少约 4 ms（用户 2026-10-08 录像）
  - [ ] 混音器里给任意一个程序单独设去处（启用按程序分流），再录一段 120 ms：____ ms（预计比上一条多约 35 ms）。另外改了 `src/Audio/RoutedCapture.cs` 或 `src/Audio/ProcessLoopback.cs` 也要重测
- [ ] Windows 10（2004–22H2）上连上 HomePod 并正常出声：日志开头 starting 行的下一行写着「Windows 10.0.19045…: no ChaCha20-Poly1305 in Windows, using our own」。Windows 10 没有系统自带的 ChaCha20-Poly1305，之前一连接就报 PlatformNotSupportedException（用户 2026-10-14 反馈）；算法本身由 ChaChaPolyTests 覆盖（RFC 8439 向量、和 Windows 自带的逐字节对比），CI 只有 Windows Server 2025，所以要有人在 Windows 10 上实测一次。改了 `src/Protocol/ChaChaPoly.cs` 才需要重测

### 二、长时间游戏与网络

- [x] 「游戏」场景玩 30 分钟：没有断音、爆音、卡顿（用户 2026-10-08 反馈：日常用安装版打游戏，没有大问题）。改了 `src/Net/RtpSender.cs`、`src/Audio/AudioFifo.cs` 或采集（`src/Audio/LoopbackCapture.cs`）才需要重测
- [ ] 首页「推流状态」下面的网络一行：平时是「网络：稳定」；卡顿后变成「网络：最近 10 分钟抖动 N 次」（单元测试：HomeNetworkTextTests、NetworkHealthTests；仍需实机：真实的卡顿有没有被记到）。改了 `src/Net/NetworkHealth.cs` 或 `src/StreamController.Network.cs` 才需要重测
- [ ] 出现黄色提示（「HomePod 那边 / 电脑这边的 Wi-Fi 最近不太稳……建议把延迟调到 … ms」）时：截图，记下时间。点「应用」：场景切到「自定义」、延迟变成建议值、重连一次，提示消失；不点就不会改延迟（单元测试：HomeNetworkTextTests、LatencyTunerTests；仍需实机：之后卡顿是否变少）。改了 `src/Net/NetworkHealth.cs` 或 `src/UI/LatencyTuner.cs` 才需要重测
- [ ] 推流中切到「音乐」再切回来：重连约 1 秒，有没有异常（单元测试：LatencyTunerTests，来回切只在停下 1 秒后重连一次；仍需实机：重连多久、有没有异常）。改了 `src/UI/LatencyTuner.cs`、`src/Scenes.cs` 或 `src/StreamController.cs` 的重连才需要重测
- [ ] 连着推流一个下午后看日志里的 `stats:` 行：音箱的 `lost=` 几乎都是 0，只有真的网络抖动时才有几个；任务管理器「详细信息」里加上「句柄」列，HomePodCast 的句柄数不再每秒涨约 20。2026-10-14 之前会连续一两个小时 `lost=100/600`、每秒一行 `ping to the speaker lost`，那是 Windows 按自己 500 ms 的节拍提前判超时，音箱其实都回了（单元测试：NetworkWatchTests；句柄：NetworkWatchSoakTests；仍需实机：真实 HomePod 上的 lost）。改了 `src/Net/NetworkWatch.cs` 才需要重测

听到卡顿时：

- 记下时间（精确到分钟）和当时在做什么。beta.5 起日志会记录 Wi-Fi 抖动（`network: ping ...`），能对上是不是网络的问题。日志（`%APPDATA%\HomePodCast\homepodcast.log`）请先复制到「下载」或项目文件夹再发，Claude 读不到 `AppData` 里的真实文件。「诊断 → 日志 → 打开日志文件夹」会直接打开日志所在的文件夹（Microsoft Store 版的日志在 `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`）
- 先确认 HomePod 连在 5G 上：路由器开着双频合一（Smart Connect）时，它可能被分到拥挤的 2.4G（2026-10-10 新家：2.4G 上 ping 中位 21 ms、丢 4%，200 和 500 ms 都卡；关掉双频合一、换到 5G 后 2 ms、0 丢包）

### 三、按键与托盘

- [ ] 「设置 → 键盘音量键」改成「推流时控制 HomePod」：推流时按音量 +/−（按住连续变）、静音键，只有 HomePod 变，Windows 音量不动，屏幕下方弹出 HomePod 音量条约 1.5 秒；断开后按键又调 Windows（单元测试：VolumeKeyModeTests；仍需实机：弹出的音量条）。改了 `src/UI/VolumeOsd.cs` 或 `src/UI/VolumeKeyHook.cs` 才需要重测
- [ ] 同一模式下进全屏游戏按音量键：游戏不失去焦点、不最小化；独占全屏时不弹音量条（单元测试：VolumeKeyModeTests，独占全屏时不弹音量条；仍需实机：游戏不失去焦点）。改了 `src/UI/VolumeOsd.cs` 或 `src/UI/VolumeKeyHook.cs` 才需要重测
- [x] 「HomePod 跟随 Windows 音量」（默认）模式下进全屏游戏按音量键：游戏没被切出去，HomePod 跟着变（用户 2026-10-08 确认；台式机，Fn 键未测）。改了 `src/UI/VolumeKeyHook.cs` 或 `src/FollowWindowsLink.cs` 才需要重测
- [x] 右键托盘图标：面板贴着图标弹出；拖音量、切场景、开关夜间模式；再右键一次能关掉（用户 2026-10-08 反馈：日常使用中没问题）。改了 `src/UI/TrayFlyout.cs` 或 `src/UI/FlyoutPlacement.cs` 才需要重测
- [x] 左键托盘图标打开主窗口（用户 2026-10-08 反馈：日常使用中没问题）。改了 `src/UI/TrayApp.cs` 的托盘图标才需要重测
- [x] 主窗口在两块缩放不同的显示器（150 % 和 100 %）之间来回拖，先在几个页面之间切换过再拖：拖动时窗口一直跟手，不出现「未响应」；松开鼠标后导航、各行和按钮按新的缩放排好，没有重影、错位；再切到别的页面，也是按新缩放排好的。拖动途中跨过去时，字会先按新缩放画、排版等松手再更新，这是有意的。拖到 100 % 那块屏上再打开首页的音箱下拉框：列表的字和下拉框一样大（1.1.0-beta.5 每次跨屏约 8 秒未响应；1.1.0 实测每次跨屏 1.1–2.3 秒，混音器页约 3 秒，100 % 屏上下拉列表的字只有三分之二大。单元测试：LayoutWorkTests 按实测的顺序模拟 Windows：先问窗口新尺寸，再逐个通知并挪动子窗口，窗口边框变了客户区也变；缩放一变就暂停整个窗口的排版、先销毁隐藏页面的窗口，只换导航和当前页的字体，松手或停下 0.15 秒后只排一次当前页，连续几次变化也只排一次，隐藏的页面在空闲时按新缩放重建；下拉列表在主屏缩放不同时字号正确；CI 自动测试：gui-smoke 在程序开着时把缩放 150 % → 100 % → 150 %；真正的跨屏拖动：用户 2026-10-17 在 #70 之后实机确认，连同在 100 % 屏上空闲预建后切到没去过的页面）。改了 `src/UI/MainWindow.cs`、`src/UI/Controls/FluentControl.cs` 的 DPI 处理或 `FluentComboBox.cs` 的下拉列表才需要重测
- [ ] 开关和换页的手感：设置 →「夜间自动断开」打开、关闭，在左侧页面之间来回切，都没有明显的停顿；程序刚启动、窗口出现几秒后，第一次打开每个页面也没有停顿（窗口显示后，空闲时会把其他页面提前建好；只在托盘里启动时不提前建）；打开后「时段」在「闲置多久后断开」上面（单元测试：LayoutWorkTests 按次数检查排版、文字测量和新建的窗口；仍需实机：真实窗口的手感）。改了 `src/UI/Controls/` 的布局、`src/UI/MainWindow.cs` 的提前建页或页面的 PageShown、Prepare 才需要重测

- [ ] 无窗口文字和纯显示容器：这次绘制重构后，在 150 %／100 % 的两块屏之间移动首页、混音器、设置，确认文字、图标、卡片、提示、日夜切换和滚动没有重影或缺字。首页跨屏消息处理及停稳后排版各小于 50 ms 是后续实测目标，窗口数量减少不能代替时间测量。2026-10-17 在 #70 后确认的双屏体验验证保留；这里复测的是之后的绘制改造。改了 `ParentPainting.cs`、`LayoutPanel.Painting.cs`、`TextBlock.cs` 的宿主绘制、`TextAutomation.cs` 或容器绘制顺序才需要重测。

### 四、夜间模式与均衡

- [x] 夜间模式开/关：爆炸、枪声变小，对白、脚步更清楚；低音减弱（用户 2026-10-08 反馈：日常使用中没问题）。改了 `src/Audio/Compressor.cs` 或 `src/Audio/Effects.cs` 才需要重测
- [ ] 均衡 → HomePod 输出均衡：「低音增强」「人声增强」听得出区别。改了 `src/Audio/Equalizer.cs` 才需要重测

### 五、使用配置和独立音量

- [ ] 保存两套配置，一键切换：声卡、去处、独立音量、EQ、音量上限和夜间模式恢复；麦克风关闭，最多重连一次（单元测试：UsageProfileTests、ProfileConnectionTests、ProfileSwitchTests、LatencyTunerTests；仍需实机：麦克风确实关了）。改了 `src/UsageProfiles.cs`、`src/UI/TrayApp.Profiles.cs` 或 `src/Audio/MicCapture.cs` 才需要重测
- [ ] 「两者」模式：HomePod 独立音量固定 60%，Windows 从 20% 调到 40%，音箱听感基本不变、本机变响；静音或 0% 两边都静音（单元测试：SendVolumeTests；仍需实机：听感）。改了 `src/Audio/SendVolumeGain.cs` 或 `src/Audio/AudioRouting.cs` 才需要重测

### 六、浏览器插件（Chrome 或 Edge）

安装：解压 `HomePodCast-extension-*.zip` → 扩展程序页 → 开启开发者模式 → 加载已解压的扩展程序。插件按钮、同步全屏、延迟按钮和快捷键、延迟画面和弹窗每次 CI 都在本地仿制的页面上测（见「已自动化」）；这里只看真实网站、口型和 60 帧是否流畅。本节各项：改了 `extension/src/`（`content.js`、`delay-engine.js`、`sync-controls.js`）才需要重测。

- [ ] 真实的 YouTube 和 bilibili 上各试一次插件按钮、「同步全屏」和网站自己的全屏（自动测试用的是本地仿制的播放器页面，真实网站的页面结构会变）。重点看「同步全屏」后网站的控制栏是否在屏幕底部、字幕／弹幕是否还在
- [ ] YouTube 一个有人说话的视频：口型和声音对得上吗？插件弹窗显示的延迟是多少
  - 2026-10-08 Edge：「影视」场景下插件弹窗显示画面延迟 237 ms、实测 238 ms，普通视频流畅；一个动画 MV 第一次打开时在 538 ms（「音乐」场景）和约 238 ms 下觉得卡，刷新后目测正常。可能是 MV 本身有低帧率段落，或者是第一次打开的冷启动（插件装好前就开着的页面、YouTube 刚开始的清晰度切换）。60 帧长延迟的情况正在用测量确认
- [x] bilibili 普通视频：237 ms 下流畅（用户 2026-10-08）
- [ ] bilibili 番剧
- [x] 0.2.1：YouTube 60 帧 MV 在「音乐」（538 ms）和「影视」（约 238 ms）下卡顿的反馈（用户刷新后复现不了）。Claude 用本地 1080p60 / 1440p60 / 2160p60 的 VP9、AV1、H.264 和 30 帧视频实测（2026-10-08，Chrome 154、240 Hz 屏、临时配置文件，见「测试记录」）：平稳播放时插件开和关一样流畅，141 / 238 / 538 ms 都不掉帧、每帧间隔一致。查出并修了两个问题：
  - WebM（YouTube 的 VP9，含 MSE）画面少延迟 12.5 ms、30 帧的 MP4 多延迟 16.7 ms：改按帧的 presentationTime 计时，各种格式都在目标值 ±1 ms 内
  - 60 帧视频、延迟超过约 650 ms（比如「音乐」再加 +110 ms 的网站偏移）时延迟画面整个冻住：改为超过约 630 ms 就隔帧存，降到 30 帧（2000 ms 时 15 帧）
  - 已知限制：网页主线程忙时（YouTube 刚打开、加载评论和推荐时最明显）延迟画面会跳帧，原视频不受影响；刷新后不卡就是这种情况
- [ ] 0.2.1：YouTube 60 帧视频（动画 MV 等）在「影视」场景看 1 分钟：流畅吗？口型对得上吗？弹窗「实测」是多少

### 七、影视场景（播放器自动对齐）

- [ ] mpv / VLC 按设置页的说明开启控制接口后：切到「影视」，播放器的音频延迟自动设好；切回别的场景自动恢复（单元测试：PlayerSyncTests、PlayerEndpointTests、CalibrationPathTests；仍需实机：真实的 mpv / VLC）。改了 `src/Players/`（`PlayerSync.cs`、`Mpv.cs`、`Vlc.cs`）才需要重测
- [ ] PotPlayer：首页显示要手动填的数值，按它设置后口型对得上（单元测试：PlayerSyncTests，首页显示的数值；仍需实机：口型）。改了 `src/Players/PlayerSync.cs` 或 `src/Players/PlayerText.cs` 才需要重测

### 八、安装包（MSI）

安装、升级、卸载、防火墙规则和向导语言，每次构建 CI 都在 GitHub 的 Windows 运行器上真的测（`msi-e2e`，见「已自动化」）；这里只有运行器上看不到的。

- [ ] 中文 Windows 上**双击** MSI：向导自动是中文（按系统显示语言选转换；CI 运行器是英文系统，只能指定 `TRANSFORMS=:2052`）；完成页勾着「启动 HomePodCast」点完成，程序自己打开。改了 `installer/`（`HomePodCast.wxs`、`build.ps1` 的语言转换、`i18n/*.wxl`）才需要重测
- [x] 双击安装只弹一次 UAC（用户 2026-10-08 确认；CI 运行器以管理员运行、没有 UAC，只能实机看）。改了 `installer/HomePodCast.wxs` 才需要重测
- [x] 装完第一次启动能直接连上 HomePod（用户 2026-10-08 确认；不弹防火墙提示现在 CI 也测）。改了 `installer/HomePodCast.wxs` 的防火墙规则或 `src/Firewall*.cs` 才需要重测
- [ ] 用浏览器从发布页下载 MSI：浏览器可能提示「不安全」，双击时 SmartScreen 可能提示「已保护你的电脑」：程序未签名、下载量少，属于预期（见 README）。应用内更新下载的 MSI 不经过浏览器、没有「来自网络」标记，应该不会有这个提示，顺便确认一下。开始签名（`build.yml` 里注释掉的 SignPath 步骤）后才需要重测

### 九、软件更新

需要 GitHub 上已经有比本机更新的版本；代理不能代为点 UAC。检查、下载、SHA-256 校验、退出、安装、带 `--updated-from=` 重新启动、只装一个、目录不变（默认和自定义目录）、清掉 `%TEMP%` 里的 `HomePodCast-update-*`，每次构建 CI 都测（`update-e2e`，见「已自动化」）。说明窗口、语言选择和关闭重开已自动化。CI 测不到的：**UAC 弹窗本身**（运行器是管理员，UAC 不弹窗）和在里面点「否」、便携版 zip、托盘提示、对真实 GitHub API 的检查。本节各项：改了 `src/Update/` 或 `src/UI/TrayApp.Update.cs` 才需要重测。

- [ ] 旧配置升级后：「关于 → 软件更新」里「自动检查更新」是关的；日志里只有 `update: automatic checks are off`，没有任何 `GET https://api.github.com/...`（单元测试：UpdateViewTests、UpdateConfigTests；新装的配置：CI 自动测试：gui-smoke）
- [ ] 点「检查更新」：状态行变成「已是最新版本」或「发现新版本 x」；日志有一行 `update: manual check ... GET https://api.github.com/repos/qiyinxi/HomePodCast/releases?per_page=20`（单元测试：UpdateViewTests、GitHubRequestTests；仍需实机：真的连上 GitHub）
- [ ] 在更新说明窗口点「更新」：状态行显示下载百分比 → 程序退出（推流停止、托盘图标消失）→ 弹出 UAC（发布者是 Windows Installer）→ 安装进度条 → HomePodCast 自己重新打开，托盘提示「已更新到 x」，「设置 → 关于」里是新版本（实机只看 UAC 弹窗、推流停止、托盘提示和「关于」；每版回归第 1 条做的就是这个，这里要在推流中做一次）
- [ ] 再来一次但在 UAC 里点「否」：旧版自己重新打开，提示「更新已取消，仍在使用当前版本。」
- [ ] 打开「自动检查更新」后重启：约 30 秒后检查一次；有新版本时托盘通知只弹一次，首页顶部出现提示条（更新 / 查看说明 / 跳过此版本）；点「跳过此版本」后提示条消失，同一版本不再提示（单元测试：UpdateViewTests、UpdateScheduleTests、ReleaseSelectionTests；仍需实机：托盘通知和提示条的样子）
- [ ] 便携版（zip）点「更新」：只打开发布页，不下载、不安装

### 十、Microsoft Store 版（MSIX，上架前）

每次构建 CI 的 `msix-e2e` 作业都会在运行器上安装打包版（用一次性证书签名的副本，发布的 msix 仍然不签名）、像开始菜单那样启动、逐项检查、再卸载（见「已自动化」和 `packaging/msix/README.md` 的 CI test 一节）。运行器上没有 HomePod、没有麦克风、也不能注销登录，下面这些还要实机测。打包版和 MSI / 便携版共用单实例锁，测之前先在托盘里退出已经开着的 HomePodCast。

准备（不需要管理员权限）：

1. 设置 → 系统 → 开发者选项：打开「开发人员模式」。
2. 在仓库根目录运行 `./packaging/msix/build.ps1 -Version 1.0.0-local.1`，再运行 `Add-AppxPackage -Register packaging\msix\obj\layout\AppxManifest.xml`（未签名的包只能这样注册）。
3. 从开始菜单启动 HomePodCast。测完卸载：`Get-AppxPackage qiyinxi.HomePodCast | Remove-AppxPackage`。

手动看防火墙规则：PowerShell 里 `Get-NetFirewallApplicationFilter | Where-Object Program -like '*\layout\HomePodCast.exe' | Get-NetFirewallRule | Format-Table DisplayName, Direction, Profile, Enabled`（这样注册的包从 `packaging\msix\obj\layout` 运行，规则写的是那里的 exe；从 Store 安装的在 `C:\Program Files\WindowsApps` 下）。

- [ ] 连接 HomePod 推流 1 分钟（音量不超过 25%）：有声音、不弹防火墙提示；日志里没有 `no rule of the package found`。改了 `packaging/msix/AppxManifest.xml` 的防火墙规则或 `src/Firewall*.cs` 才需要重测
- [ ] 混音器里给一个程序设「本机」、另一个设「HomePod」：分流照常生效（按程序分流在包里也能用）。改了 `src/Audio/ProcessLoopback.cs`、`src/Audio/RoutedCapture.cs` 或清单才需要重测
- [ ] 麦克风（没有麦克风，暂时测不了）：「麦克风 → 麦克风监听」，Windows 如果弹出是否允许 HomePodCast 使用麦克风，选允许；电平表会动、人声能推到 HomePod。改了 `src/Audio/MicCapture.cs` 或清单的 `microphone` 才需要重测
- [ ] 麦克风被拒（没有麦克风，暂时测不了）：Windows 设置 → 隐私和安全性 → 麦克风，在应用列表里关掉 HomePodCast，回到程序把麦克风关掉再打开：只弹一次「无法使用麦克风」（点「是」打开麦克风隐私设置页面），页面上麦克风下面是红字和「打开麦克风隐私设置」链接；日志里只有一行 `mic: Windows refuses the microphone (0x80070005 ...)`，没有每 2 秒一行的 `retrying`。重新允许后把麦克风关掉再打开，恢复正常。改了 `src/Audio/MicCapture.cs` 或 `src/UI/TrayApp.Effects.cs` 才需要重测
- [ ] 麦克风被拒时（没有麦克风，暂时测不了），混音器的按程序分流是否还正常（记下来：如果日志里出现 0x80070005，说明进程回环采集也受麦克风权限管）。改了 `src/Audio/ProcessLoopback.cs` 才需要重测
- [ ] MSI 版也测一次麦克风被拒（没有麦克风，暂时测不了）：关掉「允许桌面应用访问你的麦克风」，弹窗里说的是这个开关。改了 `src/Audio/MicCapture.cs` 或 `src/UI/TrayApp.Effects.cs` 才需要重测
- [ ] 「设置 → 开机自动启动」开关本身：默认是关的；在程序里打开后 Windows 设置 → 应用 → 启动 里 HomePodCast 是开的；在程序里关掉，那里也变成关（启动任务本身 CI 已测，这里看开关和 Windows 设置页面是否一致）。改了 `src/StartupTask.cs` 或 `src/UI/Pages/SettingsPage.cs` 的开关才需要重测
- [ ] 注销再登录（或重启）：HomePodCast 在托盘里启动，不弹主窗口。日志里如果有 `started by the startup task: into the tray`，说明 Windows 没传 `--tray`、靠的是启动方式判断，告诉我。改了 `src/StartupTask.cs`、`src/Program.cs` 的启动参数或清单的 `windows.startupTask` 才需要重测
- [ ] 在 Windows 设置 → 应用 → 启动 里关掉 HomePodCast，回到程序的设置页：开关变成关，下面写「已在 Windows 设置的“启动”页面里关闭，只能在那里重新打开。」；再在程序里打开它：弹窗问要不要打开 Windows 设置的「启动」页面。改了 `src/StartupTask.cs` 或 `src/UI/Pages/SettingsPage.cs` 的开关才需要重测
- [ ] 切换界面语言（设置 → 语言 → English，点「是」）：程序退出后自己重新打开、界面变成英文；新进程仍是打包版（日志 `starting from the package ...`，重启前一行是 `restarting to apply the UI language: C:\WINDOWS\explorer.exe shell:AppsFolder\qiyinxi.HomePodCast_21rkx5qfj0b6t!HomePodCast`）。CI 用同一条 `explorer.exe shell:AppsFolder\...` 命令启动打包版，所以这条命令本身能用。改了 `src/Program.cs` 的语言重启、`src/AppRestart.cs` 或 `src/AppEnvironment.cs` 才需要重测
- [ ] 「诊断 → 日志」写的路径是 `C:\Users\<你>\AppData\Local\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`；「打开日志文件夹」打开资源管理器、选中 homepodcast.log。（这台电脑以前装过 MSI 版、`%APPDATA%\HomePodCast` 里已经有日志时，打包版会接着写原来那个文件，按钮打开的就是 `%APPDATA%\HomePodCast`，也对）。改了 `src/AppEnvironment.cs` 或 `src/UI/Pages/DiagnosticsPage.cs` 的日志一栏才需要重测
- [ ] 「关于 → 软件更新」只显示「Microsoft Store 版由 Microsoft Store 负责更新。」，没有「检查更新」、更新渠道和自动检查。改了 `src/UI/Pages/SettingsPage.cs` 或 `src/UI/TrayApp.Update.cs` 才需要重测
- 上架后第一次 Store 更新时再看：推流中被更新的话，程序会不会自己回到托盘（RegisterApplicationRestart，`src/Program.cs`）

### 十一、界面语言

- [ ] 设置 → 语言切换到 Svenska / English 看一眼：翻译通不通顺、Svenska、在设置里切换后重新启动（English 和 Deutsch 每一页的截图和没翻译的中文：CI 自动测试：gui-smoke）。改了 `src/i18n/*.json` 里较多的文字、`src/UI/LanguageMenu.cs` 或 `src/AppRestart.cs` 才需要重测

### 十二、夜间自动断开（1.1 新增）

设置 → 常规 →「夜间自动断开（实验性）」。测之前把「闲置多久后断开」设成 5 分钟，时段设成包含现在的一段（比如现在 22:10，就设 22:00 到 23:00）。音量不超过 25%。本节各项：改了 `src/NightDisconnectPolicy.cs`、`src/Audio/SoundActivity.cs`、`src/UI/TrayApp.NightDisconnect.cs` 或 `src/Native.cs` 的 `SinceLastInput` 才需要重测。

1. - [ ] 推流时关掉显示器（虚拟屏照常开着），不碰键鼠、不放声音：约 5 分钟后断开（检查每 30 秒一次，最多晚 30 秒），首页和托盘提示「夜间已自动断开，23:00 自动重新连接」，日志里有 `night disconnect: no keyboard or mouse input for … min, no sound for … min`；这时 iPhone 能直接隔空播放到 HomePod（单元测试：NightDisconnectTests；仍需实机：真实的键鼠空闲时间）
2. - [ ] 不碰键鼠、但一直放视频或音乐：不断开；停掉播放 5 分钟后才断开（仍需实机：−60 dBFS 的门限对真实声音合不合适，很小声的背景音算不算有声音）
3. - [ ] 等到时段结束：30 秒内自动重新连接；如果这时 iPhone 正在用 HomePod，电脑端等它停下才连，不打断 iPhone
4. - [ ] 夜间断开后手动点「连接」：马上连上，之后再闲置 5 分钟又会断开；夜间断开后手动点过「连接」再「断开」：时段结束时不再自动连接

### 十三、同步观影窗口（1.1 新增）

音画同步 →「同步观影窗口（实验性）」，模块是单独下载的 `HomePodCast-video-window-<版本>.zip`（和程序同一版本）。测之前关掉播放器自己的音频延迟、「影视场景自动调整播放器」和浏览器插件的画面延迟。音量不超过 25%。从音画同步页打开、各语言和缩放下的布局、生成的 4K 画面经过显卡队列显示、跟随程序的延迟，已由 gui-smoke 覆盖；留哪些帧、什么时候显示、扣除多少显示延迟，由单元测试覆盖（见「已自动化」）。本节各项：改了 `modules/VideoWindow/`、`src/UI/TrayApp.VideoWindow.cs` 或 `src/Players/VideoWindowModule.cs` 才需要重测。

1. - [ ] 用不能调音频延迟的播放器（比如 Windows 的「媒体播放器」）放音画同步测试那种闪光加「咔」声的视频，模块里点「选择窗口…」选它：选择器和来源窗口有黄色边框，画面出现在模块里、保持比例、没有鼠标指针。手机录像看模块里的闪光和 HomePod 的「咔」声差多少：____ ms（仍需实机：真实的采集、显示延迟和 HomePod 的声音）
2. - [ ] 30 fps 和 60 fps 的视频各放一段，全屏看：流畅，不一顿一顿（单元测试：VideoWindowTests，30 fps 在 60.1 Hz 屏上每一帧都保留，更快的来源均匀减到 30 fps；仍需实机：看起来是否流畅）
3. - [ ] 4K 视频或 4K 屏幕全屏：任务管理器里模块的 CPU 只有几个百分点，画面清晰（构建作业的模块自检：4K 在显卡上缩到 1920 × 1080；仍需实机：真实显卡上的 CPU 和画质）
4. - [ ] 「自动扣除显示延迟」后面的毫秒数：窗口模式 ____ ms，全屏 ____ ms（一般是一次刷新上下，60 Hz 约 17 ms）；去掉勾选、设成 0 时，画面是不是晚了这么多
5. - [ ] 选模块所在的整块屏幕：模块里看不到它自己（没有画中画的递归）；停止采集后，截图工具又能截到模块窗口
6. - [ ] 改变来源窗口的大小、最小化再恢复、关掉来源窗口：画面跟着变或停止，关掉时状态栏写「源窗口已关闭，采集已停止。」；受保护的视频（比如流媒体应用里的电影）是黑的，模块不绕过
7. - [ ] 最大化后按 F11 全屏，再按 Esc：回到最大化；在两块缩放不同的显示器之间拖动模块：文字和按钮清晰、不发糊（gui-smoke：100 % 和 150 % 下按显示器感知 DPI）
8. - [ ] 换一个旧版本的模块配这个版本的 HomePodCast：窗口底部提示「模块版本 … 与 HomePodCast … 不同，请下载同一版本的模块。」（单元测试：VideoWindowTests；仍需实机：提示的样子）
9. - [ ] 关闭模块或退出 HomePodCast：模块进程退出，任务管理器里它的显存释放；打开模块时不自动采集、不开始推流

### 十四、.NET 被替换后的重启提示

只有 MSI 版和便携版会遇到（它们用电脑上装的 .NET；Microsoft Store 版自带运行时，不检查也不提示）。音量不超过 25%。

- [ ] HomePodCast 连着 HomePod 推流时，装一个新的 .NET 10 SDK 或运行时补丁版本，让它替换掉程序正在用的旧版本（2026-10-13 就是装 SDK 时把 10.0.12 换成了 10.0.13）。之后 5 分钟内（马上点「关于 → 软件更新 → 检查更新」更快）：首页最上面出现黄色提示「.NET 已更新，HomePodCast 需要重新启动才能继续正常工作。重启时声音会中断几秒，然后自动重新连接。」和「立即重启」按钮，托盘弹出一次「.NET 已更新，需要重新启动 HomePodCast。点这里查看。」；日志有 `runtime: the .NET runtime this copy started on (…) has been removed by a .NET update`。不点就什么都不发生。点「立即重启」：程序退出，几秒后自己重新打开（窗口还在，不是只在托盘），自动连回 HomePod、有声音，提示不再出现；日志里新的 starting 行之后有 `restarted by 「立即重启」: connecting again, as before`（单元测试：RuntimeWatchTests、AppRestartTests、RuntimeRestartNoticeTests；在私有的 .NET 副本上模拟替换、点「立即重启」后连回假 HomePod：CI 自动测试：gui-smoke 的 runtime 场景；仍需实机：真实的 .NET 安装程序、真实的 HomePod）。改了 `src/RuntimeWatch.cs`、`src/AppRestart.cs`、`src/UI/TrayApp.Runtime.cs` 或 `src/Program.cs` 的重启才需要重测

### 十五、仅 HomePod 输出（1.2 新增，实验性）

前提：Windows 默认输出是电脑扬声器（或耳机），「混音器 → 采集设备」跟随默认输出，首页「仅 HomePod 输出」关着、键盘音量键是默认的「HomePod 跟随 Windows 音量」。HomePod 音量不超过 25%。什么时候静音、什么时候恢复、只恢复自己设的静音、首页提示什么时候出现，已由单元测试覆盖（用模拟的默认输出，见「已自动化」）；这里看真实声卡和真实按键。本节各项：改了 `src/Audio/PcMute.cs`、`src/UI/TrayApp.PcMute.cs`、`src/UI/Pages/PcSoundNotice.cs`、`src/Audio/VolumeKeyForwarder.cs` 的通知或 `src/VolumeKeyMode.cs` 的 `Effective` 才需要重测。

1. - [ ] 连接：首页出现「电脑也在出声」和「只用 HomePod 播放」「不再提示」。点「只用 HomePod 播放」：电脑马上没声，HomePod 照常放，任务栏的音量图标显示静音；设置里键盘音量键变成「推流时控制 HomePod」，首页「仅 HomePod 输出」打开；日志有 `PC mute: muted the Windows default output …`
2. - [ ] 推流中按音量 +/−（按住连续变）和静音键：只有 HomePod 变，电脑一直没声（以前一按电脑就又响了）
3. - [ ] 点「断开」：约 1 秒后电脑恢复出声，日志 `PC mute: unmuted … again (the session ended)`；再连接又静音。连着时在托盘里退出 HomePodCast：电脑恢复出声
4. - [ ] 推流中在 Windows 里手动取消静音：电脑出声，之后（包括断线重连）这次连接里不再被静音，断开时也不动它；下一次连接又会静音
5. - [ ] 推流前先把 Windows 静音再连接、断开：Windows 一直是静音
6. - [ ] 「混音器 → 采集设备」选一个虚拟声卡（比如 VB-CABLE）再连接：电脑扬声器不被静音，首页也不提示
7. - [ ] 推流中（电脑已被静音）在任务管理器里「结束任务」HomePodCast：电脑保持静音；再打开 HomePodCast：电脑马上恢复出声，日志有 `PC mute: unmuted … left muted by the last run`

### 实验性功能（实机测过才去掉实验性）

这些功能在程序、README 和发布说明里标着「实验性」，因为还没在真实设备上测过；这种标注不随次版本号去掉，要等下面对应的部分实机测过、没有问题（`docs/RELEASING.md` 的「实验性功能」）。

#### 第三方 AirPlay 设备

第三方支持为实验性；第三方 AirPlay 2 尚未实机验证。极米 Z6X 已确认要求 FairPlay，不支持音频，不做播放测试。

- [ ] 极米 Z6X：只核对「这台设备要求 Apple 的加密，HomePodCast 不支持」提示及停止自动重试，不发送声音
- [ ] 其他可用的 AirPlay 1 接收端：维护者本人以不超过 25% 音量连接并播放音乐，分别核对 1.0 / 1.5 / 2.0 秒、音量和静音；建议先用默认 2.0 秒
- [ ] 其他可用接收端：手机投屏占用时，自动连接等待，手动连接询问；停止手机投屏后恢复；电脑或接收端休眠后可重连
- [ ] 界面：连接方式与延迟按设备记忆；换回 HomePod 后场景不变；首次兼容连接只提示一次；音画校准可保存，画面延迟超过 2000 ms 时明确受上限限制
- [ ] 其他设备反馈请附 `HomePodCast.exe scan --txt`、型号、实际协议、延迟及被拒绝请求；不要把软件接收端通过当成实机通过


- [x] 极米 Z6X 投影仪（乐播 SDK，`rhd=5.6.0.0`，自称 AppleTV3,2，`et=0,3,5`）：**不支持**。它只在 Apple 的 FairPlay 加密下才建立音频会话，不加密、RSA、TCP、PCM、AirPlay 2 式 SETUP、HomeKit 配对都只回空的 200 OK，`fp-setup` 回 FairPlay 的 142 字节应答；HomePodCast 做不了 FairPlay。现在连接时直接提示「这台设备要求 Apple 的加密，HomePodCast 不支持。」，不再重试（Claude 2026-10-14 只握手、不发音频地实测；RaopTests 覆盖）。改了 `src/Net/RaopClient.cs` 的 SETUP 处理才需要重看
- [ ] 其他第三方 AirPlay 1 / AirPlay 2 设备：连不上时请用户发日志里「RAOP SETUP reply」那一行和 `HomePodCast.exe scan --txt` 的输出

#### Apple TV：PIN 配对与推流（第 7 代，接上后再测）

Apple TV 不接受 HomePod 用的瞬时配对（对它回 470），要先在电视上配对一次：程序生成自己的身份，Apple TV 记住这台电脑；以后每次连接只做 pair-verify，不再显示数字。配对信息存在 `%APPDATA%\HomePodCast\pairings.dat`（DPAPI 加密，只有这个 Windows 用户在这台电脑上能读）。Apple TV 的家庭影院扬声器是 HomePod，所以声音会从 HomePod 出来：**测试前先在首页把音量调到 25% 以下**。本节各项：改了 `src/Protocol/HapPairing.cs`、`src/Protocol/Srp.cs`、`src/Net/Pairing.cs`、`src/Net/PairingStore.cs`、`src/StreamController.Pairing.cs`、`src/UI/PairingDialog.cs` 或 `src/UI/Pages/PairingsPanel.cs` 才需要重测。

1. - [ ] 退出 HomePodCast，在「设置」里打开「启动后自动连接」，首页选中 Apple TV 后退出，再启动：**电视不会亮、不显示数字**，也不弹配对窗口；首页状态写「〈Apple TV 名字〉需要在电视上配对一次，点「连接」开始」。日志有 `pairing: statusFlags 0x644 ask for a one-time PIN pairing`
2. - [ ] 点「连接」：弹出「在电视上配对」，写着「要在电视上配对 〈名字〉：点「开始」后，电视会显示 4 位数字，把它输入到这里。」。点「取消」：什么都不发生，电视不亮
3. - [ ] 再点「连接」→「开始」：电视亮起并显示 4 位数字（日志 `pairing: the receiver shows a PIN`），窗口里出现输入框
4. - [ ] 故意输错（比如 0000）→「配对」：窗口写「数字不对。电视上会显示新的数字，请重新输入。」，电视换成新的数字
5. - [ ] 输入正确的数字 →「配对」：窗口关闭，自动连接；HomePod 出声（家庭影院）。日志依次有 `pairing: done (pair-setup M6 verified)`、`pairing saved for ‹名字›`、`pairing: pair-verify with the stored pairing ok`、`stream ready: ... arrivalToRenderLatency=__ms`（**记下这个数**，HomePod 直连是 85）
6. - [ ] 推流开始时电视有没有被一起打开（HDMI-CEC）：____
7. - [ ] 在首页拖音量：HomePod 的音量跟着变（不超过 25%）
8. - [ ] 退出程序再启动（自动连接开着）：不显示数字、直接连上（日志只有 pair-verify，没有 `pair-pin-start`）
9. - [ ] 延迟：「游戏」场景（105 ms）做音画同步测试并录像：声音比画面晚 ____ ms；再连回 HomePod 直连录一段：____ ms（上次 118 ms）。两者差多少就是 Apple TV 转一手的代价
10. - [ ] 「设置 → 已配对的电视」里有这台 Apple TV 和配对日期；点「忘记配对」→ 确认：这一栏消失，`pairings.dat` 被删掉。再点「连接」：又弹出配对窗口
11. - [ ] 在 Apple TV 的设置里移除这台电脑（或者重置 Apple TV）后点「连接」：首页写「〈名字〉上的配对已失效，点「连接」重新配对」，配对窗口第一行写「电视上的配对已失效……」；重新配对后能连上
12. - [ ] 输错 5 次以上：窗口是否提示「输错次数太多……」（记下 Apple TV 实际的反应）
13. - [ ] 日志复制到「下载」后发我（不要包含 `pairings.dat`，那个文件不用发）

Apple TV 的其他情况（改了 `src/Net/Mdns.cs`、`src/Net/SpeakerBusy.cs`、`src/TakeoverPolicy.cs` 或上面配对的代码才需要重测）：

- [ ] `HomePodCast.exe scan --txt`：Apple TV 出现在列表里，记下它的型号和 flags
- [ ] 直接推到 Apple TV（它接电视或功放出声）：能连上、有声音；用同步测试录像，记下延迟 ____ ms
- [ ] 家庭影院模式（HomePod 设为 Apple TV 的默认扬声器）下，推到 Apple TV：HomePod 出声吗？延迟 ____ ms，和直连 HomePod 的 141 ms 比哪个低
- [ ] 家庭影院模式开着时，直连 HomePod 还能用吗？音箱报告的处理时间还是 85 ms 吗（日志里 arrivalToRenderLatency）
- [ ] Apple TV 上在播放时，电脑去连：是否会抢占、托盘状态是否正确

#### HomePod 是 Apple TV 的家庭影院音箱时（临时接管，2026-10-12 新增）

前提：卧室设为 Apple TV「客厅」（这里用你的实际名称）的默认扬声器。Apple TV 闲着时也一直占着卧室的「正在使用」标志。设置的默认值和说明、「先询问」时重启的状态、音箱信息里的文字已由单元测试覆盖（见「已自动化」）。本节各项：改了 `src/Net/HomeTheater.cs`、`src/TakeoverPolicy.cs`、`src/StreamController.Takeover.cs`、`src/UI/TrayApp.Takeover.cs` 或 `src/UI/TakeoverDialog.cs` 才需要重测。

1. - [ ] `HomePodCast.exe scan --txt`：最后出现 `home theater: 客厅 …` 和一行 `卧室 … held by it now=True`。整段发我
2. - [ ] 电视不放东西，首页点「连接」：**不弹框**，直接连上，状态是「已连接 · 卧室（临时从 Apple TV「客厅」接管，断开后交还）」，托盘提示也一样；有声音；首页「音箱处理」还是 85 ms 吗：____ ms（单元测试：TakeoverControllerTests，不弹框、直接连上和状态文字；仍需实机：有声音、音箱处理的数值）
3. - [ ] 点「断开」，然后在 Apple TV 上播放一段视频：HomePod 重新出 Apple TV 的声音吗？过了多久？要不要在 Apple TV 上做什么？（这一条决定「Apple TV 下次播放时会重新连上它」这句话对不对，**还没有在实机上验证过**）
4. - [ ] 连着时从托盘退出 HomePodCast，再在 Apple TV 上播放：同第 3 条
5. - [ ] 开着「启动后自动连接」，退出后重新打开：几秒内直接接管（状态同第 2 条）；日志里有 `held by its home-theater Apple TV: taking it over`（单元测试：TakeoverControllerTests；仍需实机：重启后真的几秒内接管）
6. - [ ] 推流时在 Apple TV 上开始播放：Apple TV 会把卧室抢回去吗？电脑端：连上 15 秒内被抢回 → 状态「卧室是 Apple TV「客厅」的家庭影院音箱，Apple TV 正连着它」并等待；超过 15 秒 → 「已断开（音箱可能被其他设备占用）」。两种都**不应来回抢**（单元测试：TakeoverControllerTests；仍需实机：Apple TV 会不会抢回去）
7. - [ ] 设置改成「先询问」，点「连接」：弹框写「卧室现在是 Apple TV「客厅」的家庭影院音箱。连接后 Apple TV 的声音会暂时断开；……」，只有「仍然连接」「取消」两个按钮；取消后什么都不变；仍然连接后状态同第 2 条（单元测试：HomeTheaterTests、TakeoverControllerTests，弹框文字和仍然连接后的状态；仍需实机：两个按钮、取消）
8. - [ ] 首页、托盘、弹框里**都没有**「改为推送到 Apple TV」之类的按钮，正常使用不会出现 PIN；音箱列表里 Apple TV 显示为「客厅（Apple TV，家庭影院：卧室）」，只有手动选它再点「连接」才会走 Apple TV 的配对（单元测试：HomeTheaterTests、PairingPolicyTests；仍需实机：首页、托盘、弹框里没有多余的按钮）
9. - [ ] 用 iPhone 往卧室推送（不是 Apple TV）：点「连接」照旧弹「比如 iPhone 或 Mac」的框；自动连接照旧等待，不会因为「临时接管」把 iPhone 挤掉。这时 `scan --txt` 里卧室那行是 `held by it now=False` 吗（单元测试：TakeoverPolicyTests、HomeTheaterTests；仍需实机：`scan --txt` 的结果）
10. - [ ] Apple TV 正在播放（比如在 Apple TV 上看视频）和空闲时各跑一次 `scan --txt`，两段都发我：看 Apple TV 和卧室的 flags、gid、pgid、igl 有没有区别（用来判断以后能不能做到「Apple TV 空闲时才接管」）
11. - [ ] 接管后重连（2026-10-16 新增）：卧室被占着（日志 `statusFlags 0xA0C04`）时点「连接」→「仍然连接」：约 5 秒后日志有 `takeover: starting our session once more`，状态闪一下「正在连接…」后回到已连接，**之后一直有声音**（2026-10-16 那次没有这一步：响一声后就没声音了，会话却一直在）。stats 行里的 `ntp=` 每分钟都在涨吗（单元测试：TakeoverRestartTests，只重连一次、断开时不再重连；仍需实机：重连后有声音）。改了 `src/StreamController.cs` 里的 `RunAsync` 才需要重测

#### 立体声对（等有两只 HomePod 配成立体声对时再测）

做法依据公开资料：两只音箱各连一路、都收到完整立体声，由这对音箱自己分左右；两路共用一个 groupUUID 和同一时间线（NTP）。「只连接主音箱」是为了验证主音箱会不会转发，默认关闭。以下命令都在安装目录运行，音量一律加 `--volume 20`。本节各项：改了 `src/Net/StereoPairs.cs`、`src/GroupPlan.cs`、`src/GroupRunner.cs`、`src/Net/SpeakerGroup.cs`、`src/StreamController.Group.cs` 或 `src/Net/RtpSender.cs` 的多路发送才需要重测。

1. - [ ] `HomePodCast.exe scan --txt`：整段输出发我。同时写下 Home App 里哪只是左、哪只是右（用来核对：flags 第 13 位标的主音箱是哪只，gid 里的 `+0+` / `+1+` 是不是对应左 / 右）
2. - [ ] `HomePodCast.exe info --host 左边IP` 和 `info --host 右边IP`：两段输出都发我（找有没有声道或主从字段）
3. - [ ] 默认模式：`HomePodCast.exe group --pair --tone --seconds 20 --volume 20 --verbose`，然后在界面里选这对音箱连接，放一段左右声道测试视频：左边只出左、右边只出右吗？两只之间有没有回声 / 一前一后的感觉？
4. - [ ] 只连主音箱：`group --pair --leader-only --tone --seconds 20 --volume 20`。另一只响不响？主音箱放的是完整立体声还是只有它那一侧？
5. - [ ] 只连非主音箱：`stream --host 非主音箱IP --tone --seconds 20 --volume 20`。只有它响，还是两只都响？
6. - [ ] 第 3 步连不上，或者一只很快断开：用 `group --pair --no-group-uuid --tone --seconds 20 --volume 20` 再试一次，看是不是 groupUUID 引起的
7. - [ ] 音量：在本程序里调音量，两只是否一起变；iPhone 控制中心里这对音箱的音量是否跟着变
8. - [ ] 默认模式连续放 30 分钟：两只有没有慢慢不同步、断开、单只没声
9. - [ ] 推流时用 iPhone 抢这对音箱：电脑端两路都退出、不抢回（单元测试：GroupRunnerTests、TakeoverControllerTests；仍需实机：真实的立体声对）

#### 多房间（等有第二台 AirPlay 音箱时再测）

侧边栏 →「多音箱」→ 电脑实时声音 → 添加另一台 HomePod、第三方 AirPlay 2 或 AirPlay 1 音箱，和当前音箱按同一时间线播放。第三方分组仍为实验性、尚未实机验证，真实延迟可能需要手动或手机录像校准；缓冲模式与 AirPlay 1 会提高整组延迟。音量一律不超过 25%。本节各项：改了 `src/GroupPlan.cs`、`src/GroupRunner.cs`、`src/UI/GroupForm.cs`、`src/StreamController.Group.cs`、`src/Net/ReceiverOutputSet.cs`、`src/Net/RaopOutputSet.cs` 或 `src/Net/RtpSender.cs` 的多路发送才需要重测。

1. - [ ] 选好第二台、确定后连接：两台都出声，首页是「已连接 · A + B（实验性）」；两台之间有没有回声 / 一前一后的感觉？
2. - [ ] 音量：主滑块让两台一起变；给一台设「音量偏移」只改它
3. - [ ] 把两台的位置分别设为左、右并应用：第一台只出左声道、第二台只出右声道；把两台位置对调后声音也对调
4. - [ ] 连续放 30 分钟：两台有没有慢慢不同步、断开、单台没声；推流时用 iPhone 抢其中一台：电脑端怎么反应、会不会来回抢（记下来）
5. - [ ] 有第三方 AirPlay 2 设备后：分别选择低延迟和缓冲方式，与 HomePod 一起播放；用手机录像或手动微调记录两台实际偏移。必需输出被接管后整组等待操作；可选输出独立恢复，缓冲延迟变大时等待重新连接，不抢回其他设备的会话。软件中的时钟、历史帧、取消和音量限制已由 ReceiverGroupControllerTests／ReceiverOutputSetTests／ReceiverTimelineSourceTests 覆盖；真实设备是否遵守时间戳仍需验证

#### 无线影院（1.1 新增，等有两台以上 HomePod 时再测）

侧边栏 →「多音箱」→ 本地影音文件：5.1／7.1 文件的每个声道送到一台 HomePod。可用一台完整立体声输出；离散声道和两台间同步仍需至少两台 HomePod。**音量一律不超过 25%**（窗口里的音量滑块就是程序的音量）。准备一个 5.1 文件（AAC 的 MP4 最常见，掩码 0x3f）和一个 7.1 文件（WAV，或者 E-AC-3 的 MKV／MP4，看 Windows 能不能解码）。声道分配、余量、窗口的音量和状态、被其他连接取代时不断开普通推流，已由单元测试覆盖（见「已自动化」）。本节各项：改了 `src/Audio/CinemaLayout.cs`、`src/Audio/CinemaFileSource.cs`、`src/Audio/CinemaMediaReader.cs`、`src/Audio/AcousticAlignment.cs`、`src/Audio/MicCapture.cs` 的校准时间戳、`src/CinemaSession.cs`、`src/UI/CinemaForm.cs`、`src/UI/TrayApp.Cinema.cs` 或 `src/Net/RtpSender.cs` 的多路发送才需要重测。

1. - [ ] 两台 HomePod 设为前左、前右，放 5.1 文件：两台都出声，对白（中置）两边都有、在中间；首页是「已连接 · 无线影院（实验性）」；日志有 `cinema: start 5.1 (0x3f) …` 和每台音箱放什么（仍需实机：声音、两台之间有没有回声或一前一后）
2. - [ ] 放 7.1 文件：能放；Windows 解不了的文件，窗口里是中文原因（不是英文异常），日志有 `cinema: not started: …`
3. - [ ] 窗口里把音量调到 10% 再点「播放」，然后换一个文件再播放：都从 10% 开始，不会跳回原来的音量；首页的音量也是 10%（单元测试：CinemaFormTests、CinemaSessionTests；仍需实机：HomePod 真的没有先响一下大声）
4. - [ ] 点「测量音箱延迟」（麦克风放在座位上）：每台依次响一声短测试音，听起来不比 25% 音量大；测完回到原来的音量；窗口写「音箱延迟校准已应用。」，日志有 `cinema calibration: … arrival … ms … → +… ms`，记下各台的延迟；Windows 任务栏的麦克风图标只在测量的几秒里出现（仍需实机：麦克风真能听到、测到的数值合不合理）
5. - [ ] 一台音箱正被 iPhone 占用时点「播放」：窗口显示在等待；这时在首页点「连接」：首页连上自己的 HomePod，影院窗口写「无线影院已停止：…」；再点影院窗口的「停止」或关掉窗口：首页的连接不断（单元测试：CinemaSessionTests；仍需实机：iPhone 占用时的真实反应）
6. - [ ] 三台以上：中置单独一台时对白只从它出来；有侧环绕或后环绕音箱时环绕声从对应的音箱出来；低音声道交给单独一台 HomePod 时它只放低频，不分配时低频混入前左前右（听感：会不会太轰或太弱）
7. - [ ] 连续放 30 分钟：各台有没有慢慢不同步、断开、单台没声；放完自动停止，最后一段声音没有被截断

#### 音乐分轨（1.1 新增）

混音器 →「音乐分轨（实验性）」：在电脑上把音乐分成人声和伴奏，分别给 HomePod 和耳机调比例。先从同一个测试版的发布页下载 `HomePodCast-stems-<版本>.zip`，解压后点「选择分轨模块」选那个文件夹。**HomePod 音量不超过 25%**，耳机音量也调小。淡入淡出、滑块平滑、缺结果时静音、开关时不重复也不咔、断开和模块出错时自动关闭、视频延迟多 186 ms、商店版没有这一项，已由单元测试覆盖；模块和发布者 PyTorch 参考结果一致、实时跟得上，由 CI 的 `stem-module` 作业测（见「已自动化」）。本节各项：改了 `src/Audio/LiveStemMixer.cs`、`src/Audio/StemTimeline.cs`、`src/Audio/StemEngine.cs`、`modules/StemWorker/` 或 `src/UI/Pages/StemPanel.cs` 才需要重测。

1. - [ ] 分离音质：放几首不同类型的歌（流行、摇滚、说唱、纯音乐），HomePod 人声 0 / 伴奏 100：人声去掉了多少？伴奏有没有明显变闷、发虚或「水声」？记下效果差的歌（仍需实机：听感）
2. - [ ] 耳机与 HomePod 对齐：打开「本机播放分轨」，本机人声 100 / 伴奏 0，HomePod 人声 0 / 伴奏 100；一只耳朵戴耳机，另一只听音箱：节奏对得上吗？耳机大概早或晚多少（听得出 / 听不出）？「游戏」和「推荐」两个场景各听一次（仍需实机：真实的耳机和 HomePod 延迟）
3. - [ ] 开关分轨、拖四个滑块：HomePod 和耳机里没有「咔」声或沙沙的拉链声；开启后约 0.2 秒静音再出声，关闭时直接回到实时、没有重复的一段（单元测试：StemTests；仍需实机：真实音乐里听不出）
4. - [ ] 推流几分钟后看混音器的状态行：每块处理多少 ms，未及时完成的帧和丢弃的块是不是一直是 0（记下数字和电脑型号）；同时打游戏时会不会开始丢块、HomePod 有没有短暂静音（仍需实机：真实电脑的负载）
5. - [ ] 分轨开着时断开再连接 HomePod：托盘提示「连接已断开，音乐分轨已关闭……」，重连后 HomePod 放的是原声；点提示打开混音器（单元测试：StemTests；仍需实机：托盘提示）
6. - [ ] 开分轨后，首页「声音比画面晚约 … ms」多 186 ms，浏览器插件弹窗的延迟也多 186 ms；看一段有人说话的视频，口型还对得上吗（单元测试：StemTests，延迟数值；仍需实机：口型）

#### DLNA 单台输出（1.2 新增，尚未实机验证）

维护者在准备使用极米 Z6X 时自行测试，步骤见 `docs/dlna.md`。命令使用临时配置，先运行 `dlna --scan` 记录 UDN；所有有声测试不超过 25%。没有麦克风也能测试，分组页已有手动微调和手机录像助手。本节各项：改了 `src/Net/Dlna*.cs`、`src/DlnaCli.cs` 才需要重测。

1. - [ ] 发现列表能找到 Z6X 的 DLNA 服务，和不支持的 FairPlay AirPlay 项分清；静音协议检查能够连接和停止
2. - [ ] 指定采集端点，以不超过 25% 音量播放；记录协商格式、分块或大 Content-Length、第一次出声的等待时间。LPCM 不支持时尝试设备声明支持的 WAV，不能播放则保留日志
3. - [ ] 连续播放 30 分钟，记录断音、设备主动停止或读取重连；音量变化和静音是否生效
4. - [ ] 由手机接管后电脑退出本次发送，不抢回；随后退出命令不会停止手机的投屏

#### 统一实时多输出分组（1.2 新增，尚未实机验证）

只由维护者在准备使用设备时测试；先确认没有 iPhone、Apple TV 或其他控制器在使用，所有有声测试上限不超过 25%。设备为
一台 HomePod 第二代、电脑内置蓝牙连接的普通 SBC 蓝牙音箱，以及极米 Z6X 的独立 DLNA 服务；Z6X 的 FairPlay AirPlay
仍不支持。用「多音箱 → 电脑实时声音」的统一输出类型和刷新入口，见 `docs/multi-output-receivers.md`、`docs/dlna.md`。
不需要麦克风。配置、界面和无声打开行为已移到「已自动化」，这里记录真实声音、蓝牙链路、设备断线和接管行为。
本节各项：改了 `src/UI/GroupPanel.Outputs.cs`、`src/UI/GroupAlignmentForm.cs`、分组控制器、`src/Net/LocalOutputSet.cs`、
`src/Net/DlnaOutputSet.cs`、`src/Audio/DlnaReadClock.cs`、`src/Audio/DlnaTimelineReader.cs` 或共同时间线才需要重测。

1. - [ ] 在 Windows 配对 SBC 蓝牙音箱，在编辑器按完整端点选择，明确标记蓝牙；200 毫秒只当起始附加延迟。HomePod + 蓝牙两路都出声，完整立体声／左／右、音量偏移听感与设置一致；不把当前采集端点选为输出
2. - [ ] HomePod + Z6X：记录 DLNA 实际协商格式、首次出声等待和估计延迟，用正负微调对齐；记录自动整组延迟，以及手动值低于下限时的实际值，只按不同房间的大致同步判断
3. - [ ] 手机用相同帧率、相同短距离分别靠近参考和目标录闪光／声音，记下两段偏移和差值；目标更晚填正、更早填负。「应用微调」后先保留草稿，再点「应用」听结果；全过程不用麦克风。只有维护者明确点「开始测试音」才发声，测试音不超过 25%
4. - [ ] HomePod + 普通 SBC 蓝牙音箱（电脑内置蓝牙）+ Z6X：记录三路刚开始、30 分钟／一小时后的偏移；时钟估计不可用时如实记录，不把 HTTP 时间或 CI 标记边界当作实际声音延迟
5. - [ ] 可选蓝牙／Z6X 断开再恢复时 HomePod 继续，不重启健康输出；手机接管或停止 DLNA 后不自动抢回，退出电脑分组不停止手机的流；设为必需后失败会停止整组。其他发送端占用的 Apple AirPlay／RAOP 设备也只等待明确操作，不为测试强抢
6. - [ ] 保存一组带名称的真实设备，重启后在首页选择再连接，确认实际去处正确；切到文件音源不会启用本机／DLNA，回到实时仍保留选择，原来单台 HomePod 的低延迟体验不变
7. - [ ] LPCM／WAV 不可用时，只试设备声明支持的 MP3／AAC，记录启动等待和编码额外偏移；连接中静音／降低音量不会恢复旧音量。沿用现有专用网络和防火墙范围，不为本测试开放公用网络或另加规则

## 已自动化（不用手测）

- **DiagnosticsPageTests / gui-smoke / gui-output-groups**：诊断页沿用原音画同步测试的导航位置，播放统计及三个排障操作可达，关于页不再显示日志和诊断报告。离屏假动作分别验证测试、日志和导出按钮的事件及所属窗口；仅打开、离开或重新打开页面不触发动作，不更改配置、连接或麦克风。CI 在全部 15 种界面语言和 150% 缩放下滚动截图并检查诊断页，不点击测试、打开日志或导出。
- **DiagnosticsPageTests**：首页移除合并的断音卡片；诊断页的缓冲欠载和网络波动分开、不相加，明确不等于可听中断；切页和重新打开不清零，连接变化在页面隐藏时仍更新基线，重连后重新计数；断开和未监测网络均显示破折号。全部 14 张翻译表同步。

- **GroupOutputEditorTests / OutputGroupSettingsTests / GroupPlanTests**：注入本机和 DLNA 发现、假主机与调用记录，检查打开／换源／等待时发现、采集、播放、校准精确为零；按完整端点 ID 和 UDN 添加、离线偏好库不激活、必需／可选、声道／音量／±5000 毫秒微调、自动／手动下限、实时／文件草稿隔离、命名分组存取、无麦克风手机帧差和手动助手只有明确测试才调用发声入口。这些配置／界面项不再列为实机重复验证。
- **OptionalAppleGroupTests / AppleGroupMemberSessionTests / RaopOutputSetTests / LocalOutputGroupTests / DlnaGroupControllerTests**：可选 Apple／RAOP／本机／DLNA 独立恢复、必需失败、忙碌／认证／接管等待明确操作、不抢回其他发送端，以及健康成员不被重启；仍需上方实机项验证设备实际反应。
- **gui-output-groups 的 group-outputs 场景**：五份独立运行完整覆盖全部 15 种界面语言、必须为 150% 缩放，自检保证每种语言恰好一次；离线保存的 AirPlay、本机、明确标记的蓝牙和有效 UDN 的 DLNA 行、未选择的偏好库、保存值与类型、展开设置、添加／移除草稿、空选择类型、自动／手动延迟、实时／文件切换、无声手动助手。每次滚动截图都检查布局，实时／文件和校准截图覆盖每种语言，额外相同过渡截图只跑一次；离页再打开仍未连接，API 的 streaming=false、保存偏好不变、没有新增采集／影院／测量／推流失败日志。发现是否精确为零由注入单元测试证明，不靠日志缺失推断。

- **DlnaDiscoveryTests / DlnaControlTests / DlnaStreamServerTests / DlnaClientTests / DlnaCliTests**：回环假设备覆盖 SSDP、描述和 SOAP、格式协商、HTTP 字节序与流量限制、Private 网络策略、音量上限、忙碌与接管、取消和连接清理。
- **DlnaReadClockTests / DlnaTimelineReaderTests / DlnaOutputSetTests / DlnaGroupControllerTests / DlnaGroupSettingsTests / DlnaEncoderTests**：四组正负漂移的一小时仿真、突发／停顿／时钟置信度失效、实际 PCM 标记、整数帧连续性和无分配读取；UDN 独立解析、可选恢复／必需失败、接管优先级、连接中音量／静音、历史覆盖恢复、编解码线程与分块边界。
- **dlna-compat-e2e / aac-fixture**：固定版本 gmrender-resurrect 使用 GStreamer 文件输出，在没有外部网络出口的 Linux 命名空间内完成 SSDP → SOAP → LPCM 分块和 WAV 大 Content-Length，比较至少 4 秒双声道 PCM；Windows 生成 MP3／AAC，Linux 独立解码并比较两种 HTTP 传输方式的设备输出，报告软件时序和编码延迟；不用真实音箱。

- **airplay-compat-e2e / aac-fixture**：经典 shairport-sync 的未加密、RSA 与密码场景，以及 shairport-sync AirPlay 2 + nqptp 的实时和 1.0 / 1.5 / 2.0 秒缓冲场景；Windows Media Foundation 生成 AAC，独立解码作参考，对齐后逐样本比较至少 6 秒，检查无缺口与请求延迟 ±50 ms。
- **ReceiverLatencySelectionTests / ThirdPartyTests / BufferedAudioTests / PtpTests**：每设备设置、HomePod 场景隔离、协议回退、接收端延迟下限、加密帧、配对、时钟选举和校准回调；**gui-smoke** 增加中英德兼容延迟与设置行的页面遍历。


每次 CI 构建都跑，失败时构建不通过、标签不发布；作业日志里每项检查一行，失败时看作业摘要和它的工件（`gui-screenshots`、`msi-e2e-logs`、`update-e2e-logs`、`msix-e2e-logs`、`extension-e2e-screenshots`）。细节见 `docs/RELEASING.md`。「实机确认过」写的是以前在真实设备上确认的日期。

- **gui-smoke**（`tests/HomePodCast.GuiSmoke`，截图在 `gui-screenshots` 工件）：这次构建的 Release 版程序，中文、English、Deutsch，100 % 和 150 % 缩放、最小窗口宽度、深色模式下，走一遍每一页（整页滚动）、托盘面板和全局快捷键、使用配置、多音箱（实验性）、同页本地文件区块（音源选择器、每台位置/延迟/偏移和接收端限制；切换音源并截图，没选文件时「播放」「暂停」「停止」「测量音箱延迟」不可用，打开和关闭不影响连接）、音箱信息：程序不退出、日志里没有 ERROR、控件没有被裁掉或互相重叠、按钮都有文字、English / Deutsch 里没有没翻译的中文；找不到音箱、等待重试（「找不到音箱：请确认它已通电、和电脑在同一网络，N 秒后重试」）时首页和托盘面板的按钮是「连接」，点了马上开始连接；连着假 HomePod 时按钮是「断开」，缓冲、音箱处理、实际延迟、重传都有数值，首页没有合并的断音计数；诊断页分别显示缓冲欠载和网络波动，未连接时均为破折号，未监测网络时不冒充 0，音箱信息读得到音箱，退出时发 TEARDOWN；首页没有「刚才卡了」「导出诊断」，诊断页有播放统计、音画同步测试、日志和诊断报告及「导出诊断」，关于页只保留版本、软件更新和许可证，全局快捷键里「标记刚才的卡顿」默认「未设置」；新装的配置「自动检查更新」是关的，「更新渠道」跟随版本（测试版、PR 和分支构建是「测试版」，正式版标签构建是「正式版」）；同步观影窗口（实验性，用这次构建的模块 zip）：从「同步观影窗口」页点「打开」直接启动模块（不弹文件夹选择、不开始采集），窗口按当前缩放显示（按显示器感知 DPI），布局和翻译同上，状态栏提示先选择窗口；再单独用生成的 4K 画面运行（`--smoke-source`，不打开窗口选择器）：画面经过显卡队列和交换链显示，缩到 1920 × 1080、按 16:9 显示，跟随程序的延迟（没推流时 0 ms，连着假 HomePod 时是它的声音延迟）；.NET 被替换（runtime 场景，只动运行器上 .NET 的私有副本）：程序在副本上启动、连着假 HomePod，副本里出现更新的补丁版本，程序用的版本没了 deps.json 和还没加载的 System.Net.Http 后点「检查更新」：软件更新一栏写「.NET 已更新，请重启 HomePodCast 后再试」，日志写明是 System.Net.Http 加载失败时发现的，首页最上面出现重启提示（推流中，带声音会中断的那句）和「立即重启」；点「立即重启」：程序退出，自己在更新的版本上重新打开窗口（不带 `--tray`），连回假 HomePod（第二次建立推流），提示消失；150 % 的中文一轮里，程序开着时把缩放改成 100 % 再改回 150 %（和拖到另一块缩放不同的显示器一样收到 WM_DPICHANGED）：窗口 5 秒内响应，导航按新 DPI 排好，布局检查通过，整窗重绘后画面不变（没有留下旧画面）
- **build 作业的模块自检**（`tools/build-video-window.ps1`，同步观影窗口的 `--self-test`，在 WARP 上、不开窗口、不采集）：32 × 16 到 8K 的已知画面经过 WinRT 表面互操作和显卡缩放后读回，尺寸和左右两半的颜色都对；大于 2560 × 1600 的来源缩小一半直到放得下（4K → 1920 × 1080，3840 × 2400、5K、8K 也能处理，原来超过 4096 × 2160 就拒绝），比显卡最大纹理还大时给出翻译过的提示
- **msi-e2e**（`tools/ci/msi-e2e.ps1`，在运行器上真的安装）：静默安装到 `C:\Program Files\HomePodCast\`，开始菜单正好一个 HomePodCast 快捷方式（没有桌面快捷方式），「设置 → 应用」里正好一个；防火墙规则只有一条、正好指向装好的 exe（入站、允许、已启用、仅专用网络、仅本地子网，和程序自己加的一样），装好的 exe 自己的启动检查（`HomePodCast.exe firewall`）确认第一次启动**不弹**防火墙提示；装到自定义目录（`INSTALLFOLDER`，路径里有空格和中文）同样成立；卸载后程序、快捷方式、规则和「应用」里的条目都没了，`%APPDATA%\HomePodCast` 里的设置（config.json、日志）原样还在；先装 v0.2.0-beta.8、v0.2.0、v0.2.1、v0.2.2、v1.0.0-beta.6、v1.0.0 和最新发布的版本再装新 MSI：旧版被替换、只有一个、是新版本、目录不变、规则仍只有一条，新旧两次安装用不同的向导语言（旧英文 → 新中文、旧中文 → 新英文交替；发布过的 MSI 从 v0.2.0-beta.2 起都是多语言的，纯英文的只在更早的 CI 构建里有过）；装着更新的版本时（比这次构建新的发布，和每次都测的、改成下一个主版本号的本 MSI 副本）新 MSI 拒绝降级、给出提示、旧版原样保留；MSI 里嵌着 `installer/build.ps1` 的全部语言转换，每种语言完成页的勾选框是它自己的文字，中文向导各页（欢迎、许可协议、安装目录、准备安装、进度、完成）都是中文、包括「启动 HomePodCast」，许可协议页是英文原文（预期），`TRANSFORMS=:2052` 真的装了中文版（msiexec 日志显示应用了内嵌的 2052 转换，快捷方式和防火墙规则的说明是中文）
- **update-e2e**（`tools/ci/update-e2e.ps1`）：装一个用同一份源码构建的旧版 MSI（0.0.1-e2e.1），用「更新」按钮的代码从 `127.0.0.1` 上的假发布列表下载这次构建的 MSI：损坏的被拒绝（不安装、不启动助手、`%TEMP%` 里不留东西）；好的校验 SHA-256、退出，安装助手通过 msiexec（Verb runas）安装，新版带 `--updated-from=` 重新启动；只装了一个、目录不变（默认和自定义目录各一遍），`%TEMP%` 里的 `HomePodCast-update-*` 被清掉（下次启动也会清）
- **extension-e2e**（`extension/test/e2e`：Chromium 加载解压的插件，本地仿制的 YouTube、bilibili 播放器页面（也有 bilibili 的 WASM 播放器 `<bwp-video>`）和一个假的 HomePodCast 本地接口，不访问真实网站；画面延迟按视频每帧里编码的帧号检查）：鼠标移到视频上才出现插件按钮，约 2.5 秒不动后消失，单按 Alt 也会出现；「同步全屏」全屏的是播放器，网站的控制栏、字幕／弹幕和延迟画面都保留，画面铺满屏幕，没有播放器包住的视频用它的父元素，退出后布局恢复；±5／±20 ms 按钮和 Alt+←/→、Alt+Shift+←/→ 改变画面延迟并保存网站偏移（按钮隐藏时也有效，光标在输入框里时不拦截），偏移不超过 ±500 ms、画面延迟在 0–2000 ms 之间；网站自己的全屏（按钮或 f 键）时插件按钮不自己出现，Alt+←/→ 生效；暂停时显示暂停的那一帧，拖进度条后不出现拖动前的画面，全屏和网页全屏时延迟画面盖住视频、继续更新；HomePodCast 未推流或没在运行时延迟画面撤掉、插件按钮不再出现，恢复推流后重新延迟；弹窗里反复开关「在此网站启用」画面正常切换（用户 2026-10-08 实机确认过）；HomePodCast 报告 538 ms 时弹窗出现「延迟较长：看视频建议用「影视」或「推荐」场景」，300 ms 及以下不出现；弹窗的状态、网站、延迟、±1 ms 和重置
- **msix-e2e**（`packaging/msix/e2e-test.ps1`，用一次性证书签名的副本）：从开始菜单的方式（`explorer.exe shell:AppsFolder\qiyinxi.HomePodCast_21rkx5qfj0b6t!HomePodCast`）启动有包标识，日志在 `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`，启动行末尾是 `starting from the package qiyinxi.HomePodCast_21rkx5qfj0b6t (Microsoft Store version)`、没有错误，真正的 `%APPDATA%\HomePodCast` 里什么都没写；安装后有两条（TCP、UDP）入站、允许、Private、已启用的规则指向包里的 exe，第一次启动没有「需要防火墙放行」、没有 UAC（日志里没有加规则的记录）、日志里没有 `no rule of the package found`；日志有 `update: Microsoft Store package: never contacts GitHub`，不安排更新检查，没有连到本机以外的 TCP 连接；开机启动任务默认关，`HomePodCast.exe autostart on|off|status`（和「设置 → 开机自动启动」开关同一段代码）打开后 Windows 记录的状态（Windows 设置 → 应用 → 启动 和任务管理器显示的就是它）是「已启用」、日志 `autostart: startup task enable requested, now Enabled`，关掉后「已禁用」、日志 `autostart: startup task disabled, now Disabled`；打包版开着、窗口关到托盘时，再从开始菜单启动它或启动 MSI 版只会叫出打包版的窗口，新进程以 0 退出、不写日志，只剩原来那个打包版进程；卸载后那两条规则、启动任务、`%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t` 都没了，没有其他名字带 HomePodCast 的开机启动项
- **stem-module**（`tools/build-stem-module.ps1`，模块 zip 在 `stem-module` 产物里）：用固定提交、校验过 SHA-256 的上游文件（有缓存时不下载）构建音乐分轨模块，和发布者独立的 PyTorch 参考结果比较 8 组（误差不超过 1e-5），用非静音输入跑性能测试，再用真实模块跑 StemModuleTests：按包的节奏实时处理 6 秒、丢块不超过 4 个、未完成的帧少于 100 ms、音频线程每次调用不到 20 ms，推理中途关闭能结束子进程，模型损坏时报「模型缺失或版本不对」（只处理缓冲区，不出声）
- **单元测试**（`tests/HomePodCast.Tests`，build 作业的 Unit tests 步骤）：
  - 音乐分轨（实验性）：模型结果缺失时，HomePod 和本机都在缺失之前 5 ms 淡出、缺失期间静音（不会退回带人声的原声）、结果恢复后淡入；拖滑块 10 ms 平滑，没有拉链声；缺结果时动了滑块，先静音再换比例；开关分轨各淡出、淡入 10 ms，开启时没有重复的一段，关闭后正好是实时原声；断流后重新开始时没有上次的延迟声音（都用合成信号检查相邻样本的最大跳变）；模型加载期间不计未完成帧；模块启动失败时自动报原因；监听设备正被采集时不打开；视频延迟多 186 ms，断开时关闭分轨，分轨开着时同步测试拒绝运行；商店版没有这一项，安装说明链接指向 main（StemTests）
  - 页面容器调整尺寸时，每层只排列一次；批量展开、收起设置行只重排一次，后面的行和滚动高度正确；文字变长但外层尺寸不变时仍重新排版；设置固定在导航栏底部，窄窗口只显示导航图标但保留无障碍名称，拉宽后恢复文字；页面列数和留白随宽度调整，宽屏内容居中，窄窗口上下排列的设置内容不被截掉；建窗口时收起、后来才展开的行和按钮回到原来的位置（LayoutTests；真实窗口的导航位置、窄窗口和页面几何检查：gui-smoke）
  - 排版工作量（真实的页面，不显示的窗口，按次数不按时间）：设置 →「夜间自动断开」打开、关闭时每个容器只排一次、每段文字只测量一次，再开一次不测量任何文字，两行按顺序出现在开关下面（原来「闲置多久后断开」跑到「时段」上面）；建好设置页以后才发现新版本时按钮是「更新」「查看说明」「跳过此版本」的顺序（原来「查看说明」在最前）；一次音箱信息读取只排一次音箱信息页；在所有页面之间切换只排要显示的那一页、不新建窗口，混音器的程序行离开再回来还是原来的（同样的程序时）；窗口换到另一块缩放的显示器（DPI 144 ↔ 96，按实测顺序模拟 Windows：先问窗口新尺寸，再逐个通知并挪动子窗口，客户区也跟着变）时变化途中一次排版都没有，隐藏页面的窗口先销毁，只有导航和当前页换字体，停下后整个窗口只排一次，控件按新 DPI 排好、没有重叠，日夜切换的淡入淡出不截图（原来每次约 8 秒未响应，1.1.0 实测仍要 1.1–2.3 秒）；连续几次变化只在最后排一次，拖动中不排、松手才排；缩放变了以后才打开的隐藏页面新建窗口、按新 DPI 只排一次；销毁了窗口的页面在空闲时按新 DPI 重建（每步不超过 100 ms），之后打开不新建窗口，没保存的输入还在；主屏缩放和窗口所在屏不同时，下拉列表的字和下拉框一样大（原来在 100 % 屏上只有三分之二）；窗口显示后空闲时提前建好的页面，第一次打开不新建窗口、只排一次，当前页和导航不受影响；只在托盘里时不提前建，用户在动鼠标时也不建（LayoutWorkTests）
  - 无窗口文字：全部页面及动态混音器行的 TextBlock 不创建子窗口，预建页面也不强制创建，DPI 变化后按宿主的字体及高度排版；文字绘制与原窗口逐像素一致，移动／换字／隐藏／移除会重绘；MSAA 和真实 UI Automation 能读取文字节点及状态值，普通按钮仍有原提供器（LayoutWorkTests、PaintedTextTests；gui-smoke 原页面检查保留，并要求首页标题和推流状态标题有无窗口 Text 节点；15 种语言仍由 gui-output-groups 覆盖）。原生 ToolTip 文字保留窗口。
  - 无窗口图标、指标和纯显示容器：图标、状态点、分隔线、仪表以及只包含显示元素的行、卡片由最近的原生宿主绘制；动态加入按钮或输入框时恢复所需的原生父窗口，保留原来的父子关系、Tab 顺序和输入处理，构造和预建阶段不提前创建窗口。嵌套绘制在浅深色下与原生控件逐像素一致，重绘范围、显隐、禁用、裁切及 MSAA 层级有回归；先读取无障碍对象再请求原生窗口时身份保留；外部 UI Automation 能读取嵌套卡片及动态只读指标（PaintedContainerTests、LayoutWorkTests）。悬停说明由原生宿主按最新位置、显隐和裁切转发（LayoutToolTip、PaintedContainerTests），gui-smoke 在中文浅深色场景悬停首页延迟卡片并检查提示。gui-smoke 通过 UIA 读取首页和诊断页的无窗口指标，仍要求连接时显示数值、闲置时显示破折号。
  - 混音器的混合行和卡片：逻辑排版树与原生父树分开，显示部分没有 HWND，输入控件挂在页面内容宿主上；含一个假程序的 fixture 从 19 个 HWND 降到 11 个，11 个排版节点无窗口、9 个输入控件仍有窗口。LayoutWorkTests 断言窗口数量、预建／切页／DPI 排版次数、浅深色整页逐像素相同；LogicalLayoutTests 覆盖逻辑父子关系、坐标、缓存 MSAA 身份、显隐、禁用、动态销毁、焦点、Tab／Shift+Tab、键盘消息、原生工具提示绑定，以及密码框内部的 HWND、IME 属性和 Tab 顺序。UIA HWND 位置覆盖只调整父子和兄弟导航，输入控件原有提供器保留；外部读取验证逻辑父节点、原生句柄、无重复节点和稳定运行时身份（LayoutWorkTests）。真实双屏耗时仍须维护者逐次批准，计数与截图不能替代实测。
  - 首页沿用同一逻辑／原生父树分离：默认假设备 fixture 从 29 个 HWND 降到 16 个，34 个排版节点无窗口，14 个实际创建的原生输入／服务控件保留。LayoutWorkTests 精确断言数量，并在浅深色、正常／窄宽度比较原生与逻辑版本的整页像素；HomeVolumeTests 沿逻辑父树检查窄行尺寸，继续验证音量、静音、仅 HomePod 输出和无真实音频写入。原有预建／切页／DPI 和 gui-smoke 首页状态、提示、无障碍及裁切断言保留。真实双屏 <50 ms 仍以逐次批准后的原始计时为准。
  - 设置页沿用逻辑／原生父树分离：默认 fixture 从21个 HWND 降到11个，13个排版节点无窗口、9个已创建的输入窗口保留。LayoutWorkTests 精确断言数量、浅深色正常／窄宽度整页像素一致，以及夜间断开行显隐和一次排版；既有 StartupTaskTests、音量键与开机启动读／写状态测试、GUI检查继续保留。测试不通过实际系统设置来验证显示逻辑；实机耗时仍须新的逐次许可。
  - 跨进程 UIA 回归核对批量缓存和逐节点读取的节点、文字、位置及属性完全相同，并覆盖校准对话框关闭、房间选择和音源切换；MSAA 容器父对象查询复用同一对象，避免大量原生代理回收时的 COM 重入（UiaProbe、LayoutWorkTests、PaintedTextTests）。批量读取途中窗口被销毁时，Windows 会返回中途截断的树（2026-10-20 实测：换回 WinForms 自带的提供器也一样，不是程序的树有错）：gui-smoke 只对这种 `CacheHelper.ParseTreeDescription` 错误在共四次读取以内重读，每次重读连同步骤写进报告，其他数组错误照常失败（PaintedTextTests）。
  - 校准后首页、插件和 mpv／VLC 使用同一补偿，切换场景时总延迟仍变化，恢复默认后回到估算；换音箱、采集声卡、默认设备或分流模式时原校准不会误用，测试中改变连接或路径不应用旧测试（CalibrationPathTests、AvCalibrationTests）
  - 刚拖动音量后立即应用不同音箱的使用配置：旧滑块值不会稍后写到新音箱，选择框跟随新音箱（ProfileSwitchTests）
  - 给「标记刚才的卡顿」设快捷键，推流时按一下再从 关于 导出 ZIP：时间和统计在，音箱／立体声成员名称、设备 ID、地址和路径被隐藏（尽力而为），组统计的数字（underruns、各成员 sent／rtx）保留，没有密码、配置或音频；没在推流时按快捷键不记录（GlitchMarkerTests、DiagnosticReportTests、HotkeyTests）
  - 「HomePod 跟随 Windows 音量」（默认）：推流时音量 +/−、静音键让 Windows 和 HomePod 一起变（Windows 100% = 音量上限），Windows 静音时 HomePod 也静音；连接时对齐到低的一边（Windows 100%、HomePod 30% 左右 → Windows 降到约 30%，再按一次音量 − HomePod 到 28 左右、不会跳到 98；Windows 12%、HomePod 24% → HomePod 降到 12%，2026-10-08 16:53 的日志验证过），在程序里拖 HomePod 音量 Windows 跟着变（VolumeKeyModeTests、FollowWindowsLinkTests；对齐和拖动用户 2026-10-08 实机确认过）
  - 「仅在 Windows 静音时」：Windows 静音时按音量 +/−、静音键，HomePod 音量跟着变，Windows 保持静音（VolumeKeyForwardingTests、VolumeKeyModeTests）
  - 快捷键 Ctrl+Alt+PageUp / PageDown / Insert / N（HotkeyTests、GlitchMarkerTests；用户 2026-10-08 实机确认过）
  - 睡眠唤醒后自动重连；iPhone 抢占 HomePod 后电脑端不抢回、托盘显示被占用（TakeoverControllerTests、TakeoverPolicyTests、GroupRunnerTests；用户 2026-10-08 实机确认过）
  - 夜间自动断开（实验性）：默认关；时段可以跨午夜（23:00–07:00 含 23:00、不含 07:00），开始和结束相同时从不断开；键鼠和声音都闲置够了才断开，只放声音或只动键鼠都不断开，时段开始前就闲置的时间也算；时段结束时只重连它自己断开的，中间手动连接或断开过就不重连，关掉这个设置也不重连；声音在所有音效之后测（HomePod 实际播放的），门限 −60 dBFS；断开后状态写「夜间已自动断开，07:00 自动重新连接」；设置能保存，旧配置没有这一项时是关的（NightDisconnectTests）
  - 测试版本机上「更新渠道」默认是「测试版」，正式版本机上默认是「正式版」；在测试版上切到「正式版」再检查（最新正式版比本机旧时）显示「当前测试版比最新正式版新……」，没有「更新」按钮（ReleaseSelectionTests、UpdateViewTests）
  - 统一多音箱页：旧 split/swap 与影院配置迁移，加载、保存、使用配置、文件播放均不意外改变实时组成员和位置，旧界面过渡期间仍能改 split/swap/offset（GroupSettingsTests）；音源限制位置、每台延迟与偏移、单台实时保留低延迟路径（GroupFormTests）；完整立体声输出保留左右并沿用环绕下混（CinemaTests）。
  - 音箱房间图：实际绘制方向符合前置、5.1 和 7.1 角度，窄／宽布局位置不重叠；空位下混去向直接采用现有混音规则；点选／键盘／声学测量高亮不启动声音；修改共用已有草稿，清空位置回到完整立体声并保留多个输出，切换立体声音源保留环绕位置（SpeakerRoomTests）。GUI 的全部 15 种语言、150% 专用分组场景检查可见视口里的位置和编辑控件，键盘与延迟草稿联动，生成的无声 5.1／7.1 文件仅检查布局并截图，不点播放、应用或测量（Walker.SpeakerRoom.cs，gui-output-groups-0 至 -4）。
  - 无线影院（实验性）：2.0、单声道、后环绕和侧环绕两种掩码的 5.1、7.1 在 2、3、5、6、7、8 台音箱上每个声道送到哪台、多大声，侧和后环绕互相补位，低音声道没有单独音箱时以 −6 dB 混入前左前右，整体余量保证不削波（CinemaTests）；Windows 解码生成的 16 位和浮点 WAV（1、2、6、8 声道，掩码 0x3f、0x60f、0x63f），解不了、不存在的文件给中文原因（CinemaTests）；所有音箱共用一条 RTP 时间线，无锁环形缓冲里每一帧都按顺序送到，暂停和测试音保持文件位置，校准结束后文件一定继续播放（CinemaTransportTests）；窗口里的音量就是开始播放的音量并保存，排队时改的音量保留，等待时在别处「连接」会结束影院、之后影院的「停止」不断开那个连接，排队中的影院停止后不再启动，测量时音箱音量不超过 25% 且之后恢复（CinemaSessionTests、CinemaFormTests）
  - 关于 → 软件更新只有一行「当前版本 x」：下面是状态，右边是「检查更新」；有新版本时同一行里换成「更新」「查看说明」「跳过此版本」（下载时「更新」变成「取消」、「跳过此版本」不能点），跳过后换回「检查更新」，下面不会多出或少掉一行；中文在默认窗口宽度下行高不变，最小宽度和德语里按钮放到文字下面，放不下时换行，都不被挤小；商店版只有状态、没有按钮（UpdateStatusRowTests；gui-smoke 检查「检查更新」在这一行里）
  - 更新说明窗口：仅手动检查的新版本主动打开，其他结果和自动检查不弹窗；简繁中文显示中文，其余语言显示英文，旧格式回退全文。跨版本按版本倒序，正式版去掉自己覆盖的测试版，20 条不足时提示查看更早变化；只开放本仓库 HTTPS 链接（ReleaseNotesTests，回环假 feed 验证只请求一次）。下载／取消／失败重试／关闭与重新打开共用进度，窗口内说明 SHA-256、管理员权限、重启和推流中断，不再二次确认（ReleaseNotesWindowTests；原 UpdatePackageTests 验证实际校验失败和取消）。gui-smoke 的 update-notes 场景使用回环假 feed，分别截图中文 100% 和英文 150% 的窗口顶部／末尾，并检查关闭重开不再请求。
  - 同步观影窗口（实验性）：30 fps 视频在 60.1 Hz 屏上每一帧都保留（原来的 33.33 ms 硬门限只剩不到 20 fps）；24–30 fps 的内容在 50–144 Hz 屏上一帧不丢，60–240 Hz 的内容均匀减到 30 fps，没有长短交替；内容暂停后重新开始不补帧；延迟太长时每秒少存几帧，不超出队列；按 60.1 Hz 刷新显示时 30 fps 的每一帧都出现，都在「采集时间 + 延迟」之后一次刷新以内；扣除的显示延迟没有帧统计时是一次刷新，有了以后用实测值，换到刷新率更低的屏幕会跟上；只跟随 HomePodCast 的本机状态，版本和模块不同时提示；启动参数按字面传递；商店版不提供这个模块（VideoWindowTests）
  - .NET 被替换后的重启提示：只检查共享的运行时（`…\shared\Microsoft.NETCore.App\<版本>`），商店版这类自带运行时的副本不检查；运行时文件夹里的 deps.json 不在了才算被替换；启动时、每 5 分钟、睡眠唤醒后、程序集加载失败时（日志写明是哪个程序集，同一个只写一次；不是程序集的文件找不到不算）各查一次，找到后停止、只通知一次；首页的提示找到之前不出现，推流时多一句声音会中断，「立即重启」只有点了才重启，中文、德语、英语在默认和最小窗口宽度下按钮不被挤小、不压住文字；重启的命令在窗口隐藏时带 `--tray`，带 `--resume=connected` 或 `--resume=idle`，商店版仍经资源管理器启动自己的包；新进程拿到这些参数和同样的环境变量（测试配置文件仍是测试配置文件），直接启动和以资源管理器为父进程（管理员运行时）都行，启动不了时说明原因（RuntimeWatchTests、AppRestartTests、RuntimeRestartNoticeTests）
  - 仅 HomePod 输出（实验性）：默认关；推流开始时把 Windows 默认输出静音，原来就静音的不算自己的、结束时不动；重试、连接中和重连时一瞬间的闲置都保持静音，闲置 1 秒以上才算会话结束，只取消自己设的、而且仍是静音的；推流中被手动取消静音后这次会话不再静音、结束时不动，下次连接重新判断；推流中打开立即静音、关闭立即恢复；默认输出换了设备时旧的恢复、新的静音，没有默认输出时放开；采集设备选了别的声卡、无线影院、分组里有本机输出就在默认设备上时都不静音；静音失败不会在结束时去取消；退出时恢复；自己的静音一开始就记进配置，交还、被手动取消后清掉，取消静音失败时留着；下次启动时（推流之前）记录还在，就把仍静音的那个设备恢复，已经取消静音的、已经不在的不动，记录都清掉；闲置且没有自己的静音时不读声卡；自己的静音用固定的事件 GUID，「跟随 Windows」不跟随它；打开时「HomePod 跟随 Windows 音量」按「推流时控制 HomePod」处理，使用配置带回「跟随 Windows」也一样，这个设置不属于使用配置。首页「电脑也在出声」只在推流、设置关、没点过「不再提示」、采集的是默认输出、没按程序分流时出现，而且默认输出是扬声器、耳机或耳麦（HDMI、S/PDIF、线路输出等不算），名字不像虚拟声卡（CABLE、VB-Audio、VoiceMeeter、Virtual、Steam Streaming、单独的 UU、虚拟），没静音、音量高于 0；「只用 HomePod 播放」打开设置、把「跟随 Windows」和「关」改成「推流时控制 HomePod」（「仅在 Windows 静音时」不变）并立即静音，「不再提示」只记下不再提示；按钮只有点了才起作用，中文、德语、英语在默认和最小窗口宽度下按钮不被挤小、不压住文字（PcMuteTests、PcMuteAppTests；全部用模拟的默认输出，不碰真实声卡）
  - 首页音量图标点击切换 HomePod 静音、状态跟随快捷键和托盘；点击前保存拖动的音量，延迟保存不会撤销静音。「仅 HomePod 输出」开关在推流时静音或恢复模拟的电脑输出，电脑音量数值和 HomePod 静音互不影响，连接前可保存，首页提示按钮也会同步开关；中文、英语、德语在窄音量卡片内两个开关的文字完整且不重叠（HomeVolumeTests）；gui-smoke 在首页检查可用的静音按钮、「仅 HomePod 输出」默认关闭和「夜间模式」开关，并确认设置页没有重复的输出开关；自检覆盖这些控件在所有语言中的名称
  - Apple TV 家庭影院（实验性）：设置 → 常规 →「HomePod 当 Apple TV 家庭影院音箱时」默认「临时接管」，下面写着连接期间 Apple TV 的声音不会从这台 HomePod 出来、断开后 Apple TV 下次播放时会重新连上它（HomeTheaterTests）；「先询问」时开着自动连接重启 HomePodCast：不连接，扫描完成后马上显示「卧室是 Apple TV「客厅」的家庭影院音箱，Apple TV 正连着它」（不先显示「正在被其他设备使用」超过几秒），这时改回「临时接管」马上连上（TakeoverControllerTests）；音箱信息里卧室的「分组」是「家庭影院：Apple TV「客厅」的音箱」、「状态」是「Apple TV「客厅」正连着它（家庭影院）」，Apple TV 的型号是「Apple TV 4K 第三代（AppleTV14,1）」、分组是「家庭影院：卧室」（SpeakerInfoTests）

## 测试记录

（Claude 的自动测试结果写在这里）

| 日期 | 测试 | 结果 |
| --- | --- | --- |
| 2026-10-08 09:49–10:19 | 30 分钟，105 ms，固定 12 ms 缓冲，「咔」声测试音，HomePod 音量 10 | 网络：迟发 1（3.4 ms）、跳包 0、重传 0。缓冲见底 25 次（约每分钟 1 次，可闻断音） |
| 2026-10-08 10:23–10:53 | 同上，自适应缓冲 | 网络：0 / 0 / 0。断音 2 次（第 1 分钟 1 次 → 16 ms；收回到 15 ms 后 10 分钟又 1 次 → 18 ms）。开头 1 分钟有 14 次溢出修剪（连上后 8 秒才开始放音） |
| 2026-10-08 10:59–11:19 | 20 分钟，起始缓冲改为 16 ms，其余同上 | 断音 0、溢出 0、迟发 0、跳包 0、重传 0；采集最长间隔 12 ms |
| 2026-10-08 16:55 | 用户手机录像（iPhone 4K60），同步测试，105 ms，安装版 beta.3 | 声音比画面晚 118 ms（115–125）。手机移近音箱时用户听到一次卡顿；录像 8 下咔声完整、间隔与闪光一致（误差 3 ms 内），视频不掉帧；日志那一分钟断音 0、重传 0 |
| 2026-10-08 16:57 | 同上，120 ms | 137 ms（124–144）。当时缓冲 20 ms：每次切场景重连被误算成断音，缓冲 +4 ms（beta.4 已修） |
| 2026-10-08 约 17:10 | 用户举手喝水时一次卡顿 | 电脑端 0 断音；17:09:30–17:10:30 HomePod 请求重发 1 个包（rtx 1/1），是 15 分钟里唯一的网络事件 → 很可能是 Wi-Fi 抖动。beta.5 加了网络监测（每 100 ms ping 音箱）。事后约 17:15 ping 50 次：中位 2 ms、最大 7 ms |
| 2026-10-08 | 日志里的其他问题 | 每次退出时托盘图标报空引用（早上一次导致崩溃）→ beta.4 已修；混音器里显示了 HomePodCast 自己 → beta.4 已隐藏 |
| 2026-10-12 | 浏览器插件自动测试 extension-e2e（Chrome for Testing 154 无头模式，本地仿 YouTube／bilibili 页面，假 HomePodCast 接口） | 25 项全过（另有 bilibili WASM 播放器 `<bwp-video>` 的仿制版：画布在封闭的 shadow root 里、由 worker 绘制，也能延迟、暂停、同步全屏）。查出并修了一个问题：点「退出全屏」后插件按钮移回页面时，Chrome 因按钮失去焦点同步触发 focusout，界面更新重入，内容脚本报错 `NotFoundError: Failed to execute 'appendChild'`（按钮最后位置是对的，但同一次更新里其他播放器的按钮没更新）。改为重入时推到下一帧再更新，并加了单元测试 |
| 2026-10-08 | 浏览器插件画面延迟的流畅度（Chrome 154 临时配置文件、240 Hz 屏，仿 YouTube 播放器 1280×720；页面内统计 + 只录测试窗口） | 插件关 / 0.2.0 / 0.2.1，1080p60 VP9·AV1·H.264、2160p60、1080p30，141 / 238 / 538 ms：视频掉帧都是 0，引擎漏帧、丢帧 0，延迟画面每帧都在第 4 个刷新周期换（30 帧为第 8 个），拷贝一帧 0.09–0.17 ms，没有长任务；录屏上 0 跳帧、没有停留超过 1.5 帧的画面，和插件关时一样（帧停留时间标准差：关 0.9–1.9 ms，开 0.9–2.2 ms）。中途接入 538 ms：先定格约 0.55 秒（与声音同步），之后均匀。延迟准确度（并排录屏，按换帧时刻）：0.2.0 时 H.264 MP4 141.5 / 250.0 / 537.4 ms，VP9 WebM 129.2 / 237.4 / 524.9 ms，MSE 的 VP9 目标 250 实际 237.2，30 帧 MP4 266.7；0.2.1 各格式中位数都正好是目标值（141.7 / 250.0 / 537.5），均值差不超过 1.2 ms。700 ms：0.2.0 延迟画面冻住（0 帧），0.2.1 为 30 帧、仍准到 700.0 ms。主线程每 0.5 秒忙 40 ms 时：原视频 0 跳帧，延迟画面跳约 10% 的帧（两版相同） |

- **导航改版**（SidebarPagesTests、LayoutWorkTests；gui-smoke）：配置页新建及应用后仍留在页面，浏览音箱详情不改变播放设备；侧栏下部日夜按钮、设置及关于固定，主要导航可滚动，紧凑模式仍有完整无障碍名称；全部页面换页复用窗口，DPI 变化只排一次；CI 逐页检查浅深色、100 %／150 %、最小宽度，并截图两次日夜切换。

- **麦克风与输出均衡分开**（MicrophonePagesTests；gui-smoke）：输出目标切换时立即更新声音去向说明，延迟显示当前设置，麦克风始终未开启；混响名称与鼠标提示齐全。麦克风均衡和输出均衡独立；输出均衡修改立即生效，夜间模式关闭后恢复修改，退出时保存、配置切换后刷新。CI 遍历麦克风和均衡两页，在浅深色、100 %／150 %、窄窗口下检查并截图，不开启真实麦克风。
