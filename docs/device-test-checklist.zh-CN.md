# 实机测试清单

分两部分：

- **每版回归**（紧接着这一节）：每个测试版发布以后做，几分钟就够，不卡发版。正式版是把测过的测试版原样转正，不用再测一遍。
- **一次性验证**（后面各节）：测过一次就记下日期，以后只有相关代码改了才重测。发测试版时，会单独列出这一版需要重测的条目。音质、夜间模式和均衡的听感、手机录像测延迟、安装向导的样子这类，都属于这一部分。能自动测的已经放进 CI，标着「CI 自动测试」或「单元测试」。

测试时 HomePod 音量不超过 25%。每一项打勾或写下现象；有问题的项写清楚“做了什么 → 看到/听到什么”。

「（单元测试：…）」里写的是 `tests/HomePodCast.Tests` 里的测试类：程序自己的逻辑已经用模拟的音箱、Apple TV、播放器和接口测过，每次 CI 构建都会跑；后面写「仍需实机」的部分（声音、画面、真实设备的反应）还要人来看。

## 每版回归（每个测试版发布后）

1. - [ ] 设置 →「软件更新」→「检查更新」→「更新」，在 UAC 里点「是」：新版自己重新打开，托盘提示「已更新到 x」。下载、校验、安装、重新打开这几步 CI 每次都自动测过，这里只是你平时装新版的操作；UAC 弹框本身只有人能看到。
2. - [ ] 连上 HomePod，用平时的延迟放一两分钟：听不到断音，首页没有黄色提示。
3. - [ ] 看一眼这一版 CI 存下的界面截图（工作流运行页面的 gui-screenshots）：有没有错位、缺字、文字被截断。

## 使用配置、校准和独立音量（新增）

- [ ] 校准后：首页、插件和 mpv／VLC 使用同一补偿；切换场景时总延迟仍变化；恢复默认后回到估算（单元测试：CalibrationPathTests、AvCalibrationTests）
- [ ] 换音箱、采集声卡、默认设备或分流模式：原校准不会误用；测试中改变连接或路径，不能应用旧测试（单元测试：CalibrationPathTests、AvCalibrationTests）
- [ ] 保存两套配置，一键切换：声卡、去处、独立音量、EQ、音量上限和夜间模式恢复；麦克风关闭，最多重连一次（单元测试：UsageProfileTests、ProfileConnectionTests、ProfileSwitchTests、LatencyTunerTests；仍需实机：麦克风确实关了）
- [ ] 刚拖动音量后立即应用不同音箱配置：旧滑块值不会稍后写到新音箱；选择框也跟随新音箱（单元测试：ProfileSwitchTests）
- [ ] 「两者」模式：HomePod 独立音量固定 60%，Windows 从 20% 调到 40%，音箱听感基本不变、本机变响；静音或 0% 两边都静音（单元测试：SendVolumeTests；仍需实机：听感）
- [ ] 给「标记刚才的卡顿」设一个快捷键，推流时按一下，再从 设置 → 工具 导出 ZIP：时间和统计存在，音箱／立体声成员名称、设备 ID、地址和路径被隐藏（尽力而为），组统计的数字（underruns、各成员 sent／rtx）保留，没有密码、配置或音频；没在推流时按快捷键不记录（单元测试：GlitchMarkerTests、DiagnosticReportTests、HotkeyTests）
- [x] 鼠标移到视频上才出现插件按钮，约 2.5 秒不动后消失；单按 Alt 也会出现。点「同步全屏」：全屏的是播放器而不是单独的视频元素，网站的控制栏、字幕／弹幕和延迟画面都保留，画面铺满屏幕；±5／±20 ms 按钮和 Alt+←/→、Alt+Shift+←/→ 改变画面延迟并保存网站偏移（按钮隐藏时快捷键也有效，光标在输入框里时不拦截），偏移不超过 ±500 ms、画面延迟在 0–2000 ms 之间；退出后布局恢复。没有播放器包住的视频，同步全屏用它的父元素（CI 自动测试：extension-e2e）
- [x] 网站自己的全屏（按钮或 f 键）：插件按钮不会自己出现，移动鼠标或按 Alt 才出现；Alt+←/→ 生效（CI 自动测试：extension-e2e）
- [ ] 真实的 YouTube 和 bilibili 上各试一次上面两条：自动测试用的是本地仿制的播放器页面，真实网站的页面结构会变。重点看「同步全屏」后网站的控制栏是否在屏幕底部、字幕／弹幕是否还在

## 已由 Claude 在 2026-10-08 测过（不用重复）

- [x] 推流回归：120 ms / 105 ms，断音、迟发、跳包、重传都是 0
- [x] 建立参数实验：HomePod 报告的处理时间固定 85 ms（只有 `moviePlayback` 模式会升到 112），现有参数已是最低
- [x] 新界面自动连接、首页推流状态（已连接、缓冲、音箱处理 85 ms、断音 0）
- [x] 30 分钟稳定性（105 ms、完整采集链路、UU 虚拟声卡）：网络侧 0 跳包 0 重传；固定 12 ms 缓冲断音 25 次 → 改成自适应缓冲后 2 次（稳定在 18 ms）。之后又加了“不退回到出过断音的大小”，见下方「测试记录」

## 一、延迟实测（手机 60 帧录像，最重要）

1. 设置 → 音画同步测试，场景选「游戏」（105 ms），手机离屏幕和音箱都约 50 cm，录 15 秒以上，**发原文件**（不要经微信压缩）。
   - [x] 105 ms：声音比画面晚 **118 ms**（8 下，115–125；目标 ≤ 145）。手机录像本身约 ±10 ms 误差
2. 场景「推荐」（120 ms）再录一段。
   - [x] 120 ms：**137 ms**（10 下，124–144；上次 156）。当时缓冲被误涨到 20 ms（beta.4 已修），修好后应再少约 4 ms
3. 混音器里给任意一个程序单独设去处（启用按程序分流），再录一段 120 ms。
   - [ ] 分流时：____ ms（预计比上一条多约 35 ms）

## 二、长时间游戏

- [x] 「游戏」场景玩 30 分钟：有没有断音、爆音、卡顿？结束后把 `%APPDATA%\HomePodCast\homepodcast.log` 发我（用户日常用安装版打游戏，2026-10-08 反馈没有大问题）
- [ ] 听到卡顿时记下时间（精确到分钟）和当时在做什么。beta.5 起日志会记录 Wi-Fi 抖动（`network: ping ...`），能对上是不是网络的问题。日志请先复制到「下载」或项目文件夹再发，Claude 读不到 `AppData` 里的真实文件。「设置 → 工具 → 日志 → 打开日志文件夹」会直接打开日志所在的文件夹（Microsoft Store 版的日志在 `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`）
- [ ] 首页「推流状态」下面的网络一行：平时是「网络：稳定」；卡顿后有没有变成「网络：最近 10 分钟抖动 N 次」（单元测试：HomeNetworkTextTests、NetworkHealthTests；仍需实机：真实的卡顿有没有被记到）
- [ ] 如果出现黄色提示（「HomePod 那边 / 电脑这边的 Wi-Fi 最近不太稳……建议把延迟调到 … ms」）：截图，记下当时的时间。点「应用」：场景切到「自定义」、延迟变成建议值、重连一次，提示消失；之后卡顿是否变少。不点就不会改延迟（单元测试：HomeNetworkTextTests、LatencyTunerTests；仍需实机：之后卡顿是否变少）
- [ ] 卡顿时先确认 HomePod 连在 5G 上：路由器开着双频合一（Smart Connect）时，它可能被分到拥挤的 2.4G（2026-10-10 新家：2.4G 上 ping 中位 21 ms、丢 4%，200 和 500 ms 都卡；关掉双频合一、换到 5G 后 2 ms、0 丢包）
- [ ] 中途切到「音乐」再切回来：重连约 1 秒，有没有异常（单元测试：LatencyTunerTests，来回切只在停下 1 秒后重连一次；仍需实机：重连多久、有没有异常）

## 三、按键与托盘

- [ ] 「设置 → 键盘音量键」=「HomePod 跟随 Windows 音量」（默认）：推流时按音量 +/−、静音键，Windows 和 HomePod 一起变（Windows 100% = 音量上限），Windows 静音时 HomePod 也静音（单元测试：VolumeKeyModeTests、FollowWindowsLinkTests）
- [x] 同一模式：连接前把 Windows 调到 100%、HomePod 存的是 30% 左右，连接后 Windows 被降到约 30%；再按一次音量 −，HomePod 到 28 左右（不会跳到 98）。在程序里拖 HomePod 音量，Windows 音量跟着变（用户 2026-10-08 确认）（单元测试：FollowWindowsLinkTests）
  - 日志已验证另一个方向：Windows 12%、HomePod 24% → 连接时 HomePod 降到 12%（2026-10-08 16:53）
- [ ] 改成「推流时控制 HomePod」：推流时按音量 +/−（按住连续变）、静音键，只有 HomePod 变，Windows 音量不动，屏幕下方弹出 HomePod 音量条约 1.5 秒；断开后按键又调 Windows（单元测试：VolumeKeyModeTests；仍需实机：弹出的音量条）
- [ ] 同一模式下进全屏游戏按音量键：游戏不失去焦点、不最小化；独占全屏时不弹音量条（单元测试：VolumeKeyModeTests，独占全屏时不弹音量条；仍需实机：游戏不失去焦点）
- [x] 「HomePod 跟随 Windows 音量」模式下进全屏游戏按音量键：游戏没被切出去，HomePod 跟着变（用户 2026-10-08 确认；台式机，Fn 键未测）
- [ ] 「仅在 Windows 静音时」：把 Windows 设为**静音**，按音量 +/−、静音键：HomePod 音量跟着变，Windows 保持静音（单元测试：VolumeKeyForwardingTests、VolumeKeyModeTests）
- [x] 快捷键 Ctrl+Alt+PageUp / PageDown / Insert / N 各按一次（用户 2026-10-08 反馈：日常使用中没问题）（单元测试：HotkeyTests、GlitchMarkerTests）
- [x] 右键托盘图标：面板贴着图标弹出；拖音量、切场景、开关夜间模式；再右键一次能关掉（用户 2026-10-08 反馈：日常使用中没问题）
- [x] 左键托盘图标打开主窗口（用户 2026-10-08 反馈：日常使用中没问题）

## 四、夜间模式与均衡

- [x] 夜间模式开/关：爆炸、枪声变小，对白、脚步更清楚；低音减弱（用户 2026-10-08 反馈：日常使用中没问题）
- [ ] 麦克风与音效 → HomePod 输出均衡：「低音增强」「人声增强」听得出区别

## 五、浏览器插件（Chrome 或 Edge）

安装：解压 `HomePodCast-extension-*.zip` → 扩展程序页 → 开启开发者模式 → 加载已解压的扩展程序。

标了「CI 自动测试：extension-e2e」的项目，每次 CI 都在 Chromium 里自动测（`extension/test/e2e`：加载解压的插件，用本地仿制的 YouTube、bilibili 播放器页面（bilibili 的也有 WASM 播放器 `<bwp-video>` 的版本）和一个假的 HomePodCast 本地接口，不访问真实网站；画面延迟按视频每帧里编码的帧号检查）。真实网站上的表现、口型对不对、60 帧是否流畅仍然要人看。

- [ ] YouTube 一个有人说话的视频：口型和声音对得上吗？插件弹窗显示的延迟是多少
  - 2026-10-08 Edge：「影视」场景下插件弹窗显示画面延迟 237 ms、实测 238 ms，普通视频流畅；一个动画 MV 第一次打开时在 538 ms（「音乐」场景）和约 238 ms 下觉得卡，刷新后目测正常。可能是 MV 本身有低帧率段落，或者是第一次打开的冷启动（插件装好前就开着的页面、YouTube 刚开始的清晰度切换）。60 帧长延迟的情况正在用测量确认
- [x] bilibili 普通视频：237 ms 下流畅（2026-10-08）
- [ ] bilibili 番剧
- [x] 播放中在弹窗里反复开关「在此网站启用」：画面正常切换（2026-10-08；CI 自动测试：extension-e2e）
- [x] 暂停、拖进度条、全屏、网页全屏：画面有没有错乱（CI 自动测试：extension-e2e：暂停时显示暂停的那一帧，拖动后不出现拖动前的画面，全屏和网页全屏时延迟画面盖住视频、继续更新）
- [x] 断开 HomePod 后：画面恢复不延迟（CI 自动测试：extension-e2e：HomePodCast 报告未推流或没在运行时，延迟画面撤掉、插件按钮不再出现；恢复推流后重新延迟）
- [x] 0.2.1：YouTube 60 帧 MV 在「音乐」（538 ms）和「影视」（约 238 ms）下卡顿的反馈（用户刷新后复现不了）。Claude 用本地 1080p60 / 1440p60 / 2160p60 的 VP9、AV1、H.264 和 30 帧视频实测（Chrome 154、240 Hz 屏、临时配置文件，见下方「测试记录」）：平稳播放时插件开和关一样流畅，141 / 238 / 538 ms 都不掉帧、每帧间隔一致。查出并修了两个问题：
  - WebM（YouTube 的 VP9，含 MSE）画面少延迟 12.5 ms、30 帧的 MP4 多延迟 16.7 ms：改按帧的 presentationTime 计时，各种格式都在目标值 ±1 ms 内
  - 60 帧视频、延迟超过约 650 ms（比如「音乐」再加 +110 ms 的网站偏移）时延迟画面整个冻住：改为超过约 630 ms 就隔帧存，降到 30 帧（2000 ms 时 15 帧）
  - 已知限制：网页主线程忙时（YouTube 刚打开、加载评论和推荐时最明显）延迟画面会跳帧，原视频不受影响；刷新后不卡就是这种情况
- [ ] 0.2.1：YouTube 60 帧视频（动画 MV 等）在「影视」场景看 1 分钟：流畅吗？口型对得上吗？弹窗「实测」是多少
- [x] 0.2.1：切到「音乐」场景时，弹窗延迟数字下面出现「延迟较长：看视频建议用「影视」或「推荐」场景」（CI 自动测试：extension-e2e：HomePodCast 报告 538 ms 时出现，300 ms 及以下不出现；弹窗的状态、网站、延迟、±1 ms 和重置也一起测）

## 六、影视场景（播放器自动对齐，完成后再测）

- [ ] mpv / VLC 按设置页的说明开启控制接口后：切到「影视」，播放器的音频延迟自动设好；切回别的场景自动恢复（单元测试：PlayerSyncTests、PlayerEndpointTests、CalibrationPathTests；仍需实机：真实的 mpv / VLC）
- [ ] PotPlayer：首页显示要手动填的数值，按它设置后口型对得上（单元测试：PlayerSyncTests，首页显示的数值；仍需实机：口型）

## 七、安装包（MSI）

- [x] 安装时可以改目录，默认 `C:\Program Files\HomePodCast\`；只弹一次 UAC
- [x] 装完第一次启动：**不再**弹防火墙提示，能直接连上
- [x] 卸载后：程序、快捷方式、防火墙规则都没了；`%APPDATA%\HomePodCast` 里的设置还在
- [ ] 下载时浏览器提示「不安全」：程序未签名、下载量少，属于预期（见 README）
- [ ] 中文 Windows 上安装向导是中文（包括完成页的「启动 HomePodCast」）；许可协议页是英文原文，这是预期的
- [ ] 用新的多语言 MSI 覆盖旧的纯英文 MSI 安装：旧版被替换，「设置 → 应用」里只有一个 HomePodCast

### 软件更新（需要 GitHub 上已经有比本机更新的版本；Claude 不能代为点 UAC，只能实机测）

CI 每次构建都会跑 `update-e2e`（`tools/ci/update-e2e.ps1`，见 `docs/RELEASING.md`「CI 里的软件更新测试」）：在运行器上装一个旧版 MSI，用和「更新」按钮相同的代码从 `127.0.0.1` 上的假发布列表下载这次构建的 MSI，校验 SHA-256，退出，安装助手通过 msiexec（Verb runas）安装，再带 `--updated-from=` 重新启动新版；默认目录和自定义目录各一遍，并确认只装了一个、目录没变、`%TEMP%` 里的 `HomePodCast-update-*` 被清掉、损坏的安装包会被拒绝。下面标「CI 已覆盖」的部分不用再手动测，只看人才能看到的东西。CI 测不到、仍要实机测的：**UAC 弹窗本身**（运行器是管理员，UAC 不弹窗）和在 UAC 里点「否」、用浏览器下载 MSI 时的 SmartScreen 提示、便携版 zip 打开发布页、确认框和托盘提示、对真实 GitHub API 的检查。

- [ ] 新装或旧配置升级后：「设置 → 软件更新」里「自动检查更新」是关的；日志里只有 `update: automatic checks are off`，没有任何 `GET https://api.github.com/...`（单元测试：UpdateViewTests、UpdateConfigTests）
- [ ] 测试版本机上「更新渠道」默认是「测试版」，正式版本机上默认是「正式版」（单元测试：ReleaseSelectionTests、UpdateViewTests）
- [ ] 点「检查更新」：状态行变成「已是最新版本」或「发现新版本 x」；日志有一行 `update: manual check ... GET https://api.github.com/repos/qiyinxi/HomePodCast/releases?per_page=20`（单元测试：UpdateViewTests、GitHubRequestTests；仍需实机：真的连上 GitHub）
- [ ] 在测试版上切到「正式版」再检查（最新正式版比本机旧时）：显示「当前测试版比最新正式版新……」，没有「更新」按钮（单元测试：UpdateViewTests、ReleaseSelectionTests）
- [ ] 推流时点「更新」：确认框里有「正在推流：推送会停止大约一分钟。」；点「否」什么都不发生（单元测试：UpdateViewTests，确认框的文字；仍需实机：点「否」）
- [ ] 点「是」：状态行显示下载百分比 → 程序退出（推流停止、托盘图标消失）→ 弹出 UAC（发布者是 Windows Installer）→ 安装进度条 → HomePodCast 自己重新打开，托盘提示「已更新到 x」，「设置 → 关于」里是新版本；安装目录没变（装在自定义目录时也一样），开始菜单只有一个 HomePodCast（CI 已覆盖：下载、校验、退出、安装、重新启动、只装一个、目录不变；实机只看 UAC 弹窗、推流停止、托盘提示和「关于」）
- [ ] 再来一次但在 UAC 里点「否」：旧版自己重新打开，提示「更新已取消，仍在使用当前版本。」（只能实机测）
- [ ] `%TEMP%` 里没有残留的 `HomePodCast-update-*` 文件夹（下次启动会清掉）（CI 已覆盖）
- [ ] 打开「自动检查更新」后重启：约 30 秒后检查一次；有新版本时托盘通知只弹一次，首页顶部出现提示条（更新 / 查看说明 / 跳过此版本）；点「跳过此版本」后提示条消失，同一版本不再提示（单元测试：UpdateViewTests、UpdateScheduleTests、ReleaseSelectionTests；仍需实机：托盘通知和提示条的样子）
- [ ] 便携版（zip）点「更新」：只打开发布页，不下载、不安装（只能实机测）
- [ ] 用浏览器从发布页下载 MSI 再双击：SmartScreen 可能提示「已保护你的电脑」（程序未签名，属于预期）；应用内更新下载的 MSI 不经过浏览器、没有「来自网络」标记，应该不会有这个提示，顺便确认一下（只能实机测）

## 八、其他

- [x] 睡眠唤醒后自动重连（用户 2026-10-08 反馈：日常使用中没问题）（单元测试：TakeoverControllerTests、TakeoverPolicyTests）
- [x] iPhone 抢占 HomePod 后，电脑端不抢回；托盘显示被占用（用户 2026-10-08 反馈：日常使用中没问题）（单元测试：TakeoverControllerTests、GroupRunnerTests）
- [ ] 设置 → 语言切换到 Svenska / English 看一眼，有没有明显不通的翻译

## 九、Apple TV（第 7 代，接上后再测）

### PIN 配对与推流（新增）

Apple TV 不接受 HomePod 用的瞬时配对（对它回 470），要先在电视上配对一次：程序生成自己的身份，Apple TV 记住这台电脑；以后每次连接只做 pair-verify，不再显示数字。配对信息存在 `%APPDATA%\HomePodCast\pairings.dat`（DPAPI 加密，只有这个 Windows 用户在这台电脑上能读）。Apple TV 的家庭影院扬声器是 HomePod，所以声音会从 HomePod 出来：**测试前先在首页把音量调到 25% 以下**。

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

### 其他

- [ ] `HomePodCast.exe scan --txt`：Apple TV 出现在列表里，记下它的型号和 flags
- [ ] 直接推到 Apple TV（它接电视或功放出声）：能连上、有声音；用同步测试录像，记下延迟 ____ ms
- [ ] 家庭影院模式（HomePod 设为 Apple TV 的默认扬声器）下，推到 Apple TV：HomePod 出声吗？延迟 ____ ms，和直连 HomePod 的 141 ms 比哪个低
- [ ] 家庭影院模式开着时，直连 HomePod 还能用吗？音箱报告的处理时间还是 85 ms 吗（日志里 arrivalToRenderLatency）
- [ ] Apple TV 上在播放时，电脑去连：是否会抢占、托盘状态是否正确

### HomePod 是 Apple TV 的家庭影院音箱时（临时接管，2026-10-12 新增）

前提：卧室设为 Apple TV「客厅」（这里用你的实际名称）的默认扬声器。Apple TV 闲着时也一直占着卧室的「正在使用」标志。

1. - [ ] `HomePodCast.exe scan --txt`：最后出现 `home theater: 客厅 …` 和一行 `卧室 … held by it now=True`。整段发我
2. - [ ] 设置 → 常规 →「HomePod 当 Apple TV 家庭影院音箱时」：默认选「临时接管」，下面写着连接期间 Apple TV 的声音不会从这台 HomePod 出来、断开后 Apple TV 下次播放时会重新连上它（单元测试：HomeTheaterTests）
3. - [ ] 电视不放东西，首页点「连接」：**不弹框**，直接连上，状态是「已连接 · 卧室（临时从 Apple TV「客厅」接管，断开后交还）」，托盘提示也一样；有声音；首页「音箱处理」还是 85 ms 吗：____ ms（单元测试：TakeoverControllerTests，不弹框、直接连上和状态文字；仍需实机：有声音、音箱处理的数值）
4. - [ ] 点「断开」，然后在 Apple TV 上播放一段视频：HomePod 重新出 Apple TV 的声音吗？过了多久？要不要在 Apple TV 上做什么？（这一条决定「Apple TV 下次播放时会重新连上它」这句话对不对，**还没有在实机上验证过**）
5. - [ ] 连着时从托盘退出 HomePodCast，再在 Apple TV 上播放：同第 4 条
6. - [ ] 开着「启动后自动连接」，退出后重新打开：几秒内直接接管（状态同第 3 条）；日志里有 `held by its home-theater Apple TV: taking it over`（单元测试：TakeoverControllerTests；仍需实机：重启后真的几秒内接管）
7. - [ ] 推流时在 Apple TV 上开始播放：Apple TV 会把卧室抢回去吗？电脑端：连上 15 秒内被抢回 → 状态「卧室是 Apple TV「客厅」的家庭影院音箱，Apple TV 正连着它」并等待；超过 15 秒 → 「已断开（音箱可能被其他设备占用）」。两种都**不应来回抢**（单元测试：TakeoverControllerTests；仍需实机：Apple TV 会不会抢回去）
8. - [ ] 设置改成「先询问」，点「连接」：弹框写「卧室现在是 Apple TV「客厅」的家庭影院音箱。连接后 Apple TV 的声音会暂时断开；……」，只有「仍然连接」「取消」两个按钮；取消后什么都不变；仍然连接后状态同第 3 条（单元测试：HomeTheaterTests、TakeoverControllerTests，弹框文字和仍然连接后的状态；仍需实机：两个按钮、取消）
9. - [ ] 「先询问」时重启 HomePodCast（开着自动连接）：不连接，状态显示「卧室是 Apple TV「客厅」的家庭影院音箱，Apple TV 正连着它」（扫描完成后马上显示，不先显示「正在被其他设备使用」超过几秒）；这时把设置改回「临时接管」：马上连上（单元测试：TakeoverControllerTests）
10. - [ ] 首页、托盘、弹框里**都没有**「改为推送到 Apple TV」之类的按钮，正常使用不会出现 PIN；音箱列表里 Apple TV 显示为「客厅（Apple TV，家庭影院：卧室）」，只有手动选它再点「连接」才会走 Apple TV 的配对（单元测试：HomeTheaterTests、PairingPolicyTests；仍需实机：首页、托盘、弹框里没有多余的按钮）
11. - [ ] 设置 → 音箱信息：卧室的「分组」是「家庭影院：Apple TV「客厅」的音箱」，「状态」是「Apple TV「客厅」正连着它（家庭影院）」；选 Apple TV 时型号是「Apple TV 4K 第三代（AppleTV14,1）」，分组是「家庭影院：卧室」（单元测试：SpeakerInfoTests）
12. - [ ] 用 iPhone 往卧室推送（不是 Apple TV）：点「连接」照旧弹「比如 iPhone 或 Mac」的框；自动连接照旧等待，不会因为「临时接管」把 iPhone 挤掉。这时 `scan --txt` 里卧室那行是 `held by it now=False` 吗（单元测试：TakeoverPolicyTests、HomeTheaterTests；仍需实机：`scan --txt` 的结果）
13. - [ ] Apple TV 正在播放（比如在 Apple TV 上看视频）和空闲时各跑一次 `scan --txt`，两段都发我：看 Apple TV 和卧室的 flags、gid、pgid、igl 有没有区别（用来判断以后能不能做到「Apple TV 空闲时才接管」）

## 十、立体声对（等有两只 HomePod 配成立体声对时再测）

做法依据公开资料：两只音箱各连一路、都收到完整立体声，由这对音箱自己分左右；两路共用一个 groupUUID 和同一时间线（NTP）。「只连接主音箱」是为了验证主音箱会不会转发，默认关闭。以下命令都在安装目录运行，音量一律加 `--volume 20`。

1. - [ ] `HomePodCast.exe scan --txt`：整段输出发我。同时写下 Home App 里哪只是左、哪只是右（用来核对：flags 第 13 位标的主音箱是哪只，gid 里的 `+0+` / `+1+` 是不是对应左 / 右）
2. - [ ] `HomePodCast.exe info --host 左边IP` 和 `info --host 右边IP`：两段输出都发我（找有没有声道或主从字段）
3. - [ ] 默认模式：`HomePodCast.exe group --pair --tone --seconds 20 --volume 20 --verbose`，然后在界面里选这对音箱连接，放一段左右声道测试视频：左边只出左、右边只出右吗？两只之间有没有回声 / 一前一后的感觉？
4. - [ ] 只连主音箱：`group --pair --leader-only --tone --seconds 20 --volume 20`。另一只响不响？主音箱放的是完整立体声还是只有它那一侧？
5. - [ ] 只连非主音箱：`stream --host 非主音箱IP --tone --seconds 20 --volume 20`。只有它响，还是两只都响？
6. - [ ] 第 3 步连不上，或者一只很快断开：用 `group --pair --no-group-uuid --tone --seconds 20 --volume 20` 再试一次，看是不是 groupUUID 引起的
7. - [ ] 音量：在本程序里调音量，两只是否一起变；iPhone 控制中心里这对音箱的音量是否跟着变
8. - [ ] 默认模式连续放 30 分钟：两只有没有慢慢不同步、断开、单只没声
9. - [ ] 推流时用 iPhone 抢这对音箱：电脑端两路都退出、不抢回（单元测试：GroupRunnerTests、TakeoverControllerTests；仍需实机：真实的立体声对）

## 十一、Microsoft Store 版（MSIX，1.0.0 上架前）

Claude 只能打包、不能安装和运行打包版，下面都要实机测。打包版和 MSI / 便携版共用单实例锁，测之前先在托盘里退出已经开着的 HomePodCast。

准备（不需要管理员权限）：

1. 设置 → 系统 → 开发者选项：打开「开发人员模式」。
2. 在仓库根目录运行 `./packaging/msix/build.ps1 -Version 1.0.0-local.1`，再运行 `Add-AppxPackage -Register packaging\msix\obj\layout\AppxManifest.xml`（未签名的包只能这样注册）。
3. 从开始菜单启动 HomePodCast。测完卸载：`Get-AppxPackage qiyinxi.HomePodCast | Remove-AppxPackage`。

- [ ] 日志的启动行末尾是 `starting from the package qiyinxi.HomePodCast_21rkx5qfj0b6t (Microsoft Store version)`（「设置 → 工具 → 日志 → 打开日志文件夹」）
- [ ] 防火墙：第一次启动**没有**「需要防火墙放行」，也没有 UAC。PowerShell 里 `Get-NetFirewallApplicationFilter | Where-Object Program -like '*\layout\HomePodCast.exe' | Get-NetFirewallRule | Format-Table DisplayName, Direction, Profile, Enabled` 列出两条（TCP、UDP）入站、Private、已启用的规则（这样注册的包从 `packaging\msix\obj\layout` 运行，规则写的是那里的 exe；从 Store 安装的在 `C:\Program Files\WindowsApps` 下）
- [ ] 连接 HomePod 推流 1 分钟（音量不超过 25%）：有声音、不弹防火墙提示；日志里没有 `no rule of the package found`
- [ ] 混音器里给一个程序设「本机」、另一个设「HomePod」：分流照常生效（按程序分流在包里也能用）
- [ ] 麦克风：「麦克风与音效 → 开启麦克风」，Windows 如果弹出是否允许 HomePodCast 使用麦克风，选允许；电平表会动、人声能推到 HomePod
- [ ] 麦克风被拒：Windows 设置 → 隐私和安全性 → 麦克风，在应用列表里关掉 HomePodCast，回到程序把麦克风关掉再打开：只弹一次「无法使用麦克风」（点「是」打开麦克风隐私设置页面），页面上麦克风下面是红字和「打开麦克风隐私设置」链接；日志里只有一行 `mic: Windows refuses the microphone (0x80070005 ...)`，没有每 2 秒一行的 `retrying`。重新允许后把麦克风关掉再打开，恢复正常
- [ ] 麦克风被拒时，混音器的按程序分流是否还正常（记下来：如果日志里出现 0x80070005，说明进程回环采集也受麦克风权限管）
- [ ] MSI 版也测一次麦克风被拒：关掉「允许桌面应用访问你的麦克风」，弹窗里说的是这个开关
- [ ] 开机自动启动：「设置 → 开机自动启动」默认是关的；打开后 Windows 设置 → 应用 → 启动 里 HomePodCast 是开的，日志有 `autostart: startup task enable requested, now Enabled`
- [ ] 注销再登录（或重启）：HomePodCast 在托盘里启动，不弹主窗口。日志里如果有 `started by the startup task: into the tray`，说明 Windows 没传 `--tray`、靠的是启动方式判断，告诉我
- [ ] 在 Windows 设置 → 应用 → 启动 里关掉 HomePodCast，回到程序的设置页：开关变成关，下面写「已在 Windows 设置的“启动”页面里关闭，只能在那里重新打开。」；再在程序里打开它：弹窗问要不要打开 Windows 设置的「启动」页面
- [ ] 在程序里关掉开机自动启动：Windows 设置的「启动」页面里也变成关
- [ ] 切换界面语言（设置 → 语言 → English，点「是」）：程序退出后自己重新打开、界面变成英文；新进程仍是打包版（日志 `starting from the package ...`，重启前一行是 `restarting to apply the UI language: C:\WINDOWS\explorer.exe shell:AppsFolder\qiyinxi.HomePodCast_21rkx5qfj0b6t!HomePodCast`）
- [ ] 「设置 → 工具 → 日志」写的路径是 `C:\Users\<你>\AppData\Local\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`；「打开日志文件夹」打开资源管理器、选中 homepodcast.log。（这台电脑以前装过 MSI 版、`%APPDATA%\HomePodCast` 里已经有日志时，打包版会接着写原来那个文件，按钮打开的就是 `%APPDATA%\HomePodCast`，也对）
- [ ] 「设置 → 软件更新」只显示「Microsoft Store 版由 Microsoft Store 负责更新。」，没有「检查更新」、更新渠道和自动检查；日志有 `update: Microsoft Store package: never contacts GitHub`
- [ ] 打包版开着时再启动 MSI 版：只会把打包版的窗口叫出来
- [ ] 卸载后：Windows 防火墙里那两条规则、「启动」页面里的 HomePodCast、`%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t` 都没了

上架后第一次 Store 更新时再看：推流中被更新的话，程序会不会自己回到托盘（RegisterApplicationRestart）。

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
