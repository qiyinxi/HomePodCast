# Live output groups / 实时输出分组

## English

Experimental since 1.2; third-party groups have not been tested on hardware. Live PC audio can combine HomePods, Apple TV,
third-party AirPlay 2 realtime or buffered receivers, AirPlay 1, Windows render endpoints and DLNA. The unified
picker is on the Multi-speaker page. A single HomePod retains its original transient pairing, NTP, realtime PCM and scene path. Local-file groups still
require HomePods. FairPlay-only devices remain unsupported; their separate DLNA service may work.

Choose **Live computer audio**, an **Output type**, then **Refresh outputs** and **Add output**. Local/DLNA discovery
is explicit: opening the page, waiting or switching sources starts no discovery, capture, playback, test tone or
microphone. Up to eight outputs can be selected. Windows outputs use exact endpoint IDs, including punctuation.
Pair Bluetooth in Windows first, select its endpoint, then explicitly mark **Bluetooth output** under **More settings**;
the name does not determine its type. A local output matching the capture endpoint is refused to prevent feedback.
Windows does not report Bluetooth link latency: 200 ms is only a starting estimate for an endpoint without a saved
estimate, not a measurement or a guaranteed value.

Every live output has full stereo/left/right channels, volume offset, **Required output**, and signed trim from
−5000 to +5000 ms; positive trim plays later. Local outputs also have extra link latency; DLNA has estimated receiver
latency, preferred format and HTTP framing. **Group delay** can be automatic or manual: automatic accommodates the
slowest output, negative trims and producer headroom; a manual value still respects negotiated device floors. Home
shows the actual delay. **Apply** updates live choices; **Save group** stores a named output/synchronization snapshot.
Select it under **Saved groups** on Home, then connect explicitly. Saving, selecting while idle, or remembering an
offline preference-bank entry never connects it. Preference banks and selected IDs are separate.

Switching to **Local media file** hides local/DLNA live rows and disables their discovery while retaining their saved
choices. File positions and 0–500 ms delays remain separate from live channels and signed trims. Editing live outputs
or saving a named group does not start a file or activate its layout.

| Output | Synchronization target |
| --- | --- |
| AirPlay 2 | Shared NTP/PTP timeline, aiming for tight synchronization; third-party hardware still needs verification |
| AirPlay 1 / ordinary Bluetooth / local | Basic alignment after calibration; buffering or Bluetooth adds group delay |
| DLNA | Rough alignment for different rooms; real receiver delay and acoustic drift need measurement |

**Manual calibration** opens silently and needs no microphone. Its optional **Start test tone** is an explicit action
after applying and connecting the group, capped at 25%, with no microphone. The cap is applied before tones start and
kept until queued tones drain; closing the test window stops new tones immediately. Record flash and sound separately near
the reference speaker and target speaker at the same short distance and frame rate. Count sound-minus-flash frames
for each video, then enter `target offset − reference offset`: positive if the target is later, negative if earlier.
The helper computes `new target trim = old target trim − relative frames × 1000 / fps`, rounded to milliseconds,
within ±5000 ms. For example, 8 and 11 frames at 60 fps mean +3 relative frames and 50 ms less target trim.
**Apply trim** changes only the editor draft; **Apply** on the group page makes it effective. File playback's
microphone-based acoustic measurement remains a separate optional feature. Never reclaim a busy speaker to calibrate it.

One capture/effects producer writes the shared history. Every transport reads the frames due at capture time plus
the group delay and its saved trim. The group delay accommodates the slowest negotiated transport plus producer
headroom; extra delay is stored in history rather than an oversized receiver buffer. Realtime transports use
352-frame packets; buffered AAC uses 1024-frame blocks. Integer frame positions prevent cumulative rounding gaps.
NTP and group PTP project the same monotonic clock, with one shared PTP master per local interface and group.

The protocol override and fallback order match standalone receivers. Preparation does not consume live audio.
All transports start on a common future epoch. Required outputs stop the group on failure; optional Apple AirPlay,
RAOP, local and DLNA outputs recover independently after recoverable transport loss without restarting healthy outputs.
A slower optional fallback pauses until the group is
reconnected with sufficient delay. Busy, password, pairing and takeover failures wait for explicit action;
automatic retries do not reclaim another sender's session. Volume offsets, mute and the current safety cap apply
to every transport, including changes made while connecting. These controls do not change Windows endpoint volume.
DLNA keeps its existing Private-network listener and approved firewall scope; no new rule or Public access is added.

Software tests verify shared presentation timestamps, packet continuity, channel mapping, recovery, caps and
takeover behavior. CI additionally sends the production coordinated realtime/buffered paths to pinned
shairport-sync/nqptp, compares decoded PCM with an independent reference and checks requested delay within 50 ms.
This establishes software interoperability, not a measured hardware synchronization guarantee. Third-party
receivers can add undisclosed processing, so validate and trim by ear or phone video; no microphone is required.
GroupOutputEditorTests uses injected spies for exact zero discovery/capture/playback/calibration calls on opening,
offline banks, endpoint/UDN selection and draft calibration. OutputGroupSettingsTests covers named groups and selection
storage. gui-smoke implements focused offline scenarios in all 15 languages at required 150% scale, both sources and
the silent helper, with layout checks at every captured scroll. These software checks do not establish acoustic bounds;
real testing remains on the maintainer's [device checklist](device-test-checklist.zh-CN.md).

## 简体中文

1.2 起为实验性；第三方分组尚未在真实设备上测试。电脑实时声音可以混用 HomePod、Apple TV、第三方 AirPlay 2 实时或缓冲接收端、
AirPlay 1、Windows 播放端点和 DLNA，统一选择器位于「多音箱」页。单台 HomePod 保留原有瞬时配对、NTP、实时 PCM 和场景路径。
本地文件分组仍只支持 HomePod。只认 FairPlay 的设备始终不支持，但它独立提供的 DLNA 服务可能可用。

选择「电脑实时声音」和「输出类型」，再点「刷新输出」「添加输出」，最多八路。发现本机／DLNA 必须明确刷新；打开页面、
等待或切换音源不会发现设备，也不会启动采集、播放、测试音或麦克风。Windows 输出按完整端点 ID 识别，标点也属于身份。
蓝牙音箱先在 Windows 配对，选对应端点，再在「更多设置」明确标记「蓝牙输出」，不会按名称猜类型；本机输出不能与采集端点
相同，以免回授。Windows 不报告蓝牙链路延迟：尚无保存估计值时填入的 200 毫秒只是起点，不是测量结果，也不保证适用。

每路实时输出可设完整立体声／左／右、音量偏移、「必需输出」和 −5000 到 +5000 毫秒微调，正值更晚播放。本机另有附加
链路延迟；DLNA 可设估计接收延迟、首选格式和 HTTP 传输。「整组延迟」可自动或手动：自动覆盖最慢输出、负微调和生产余量，
手动仍遵守设备协商下限，首页显示实际值。「应用」更新实时选择，「保存分组」保存有名称的输出／同步快照，再在首页
「已保存的分组」选择并明确连接。保存、闲置时选择分组或记住离线偏好库都不会连接；偏好库与选中的 ID 分开保存。

切到「本地影音文件」会隐藏本机／DLNA 实时行并禁用发现，但保留选择。文件位置和 0–500 毫秒延迟与实时声道、正负微调
互相独立；编辑实时输出或保存命名分组不会播放文件或启用文件布局。

| 输出 | 同步目标 |
| --- | --- |
| AirPlay 2 | 共用 NTP／PTP 时间线，目标严格同步；第三方硬件仍需验证 |
| AirPlay 1／普通蓝牙／本机 | 校准后基本对齐；缓冲或蓝牙增加整组延迟 |
| DLNA | 适合不同房间的大致对齐；真实接收延迟和声音漂移需要实测 |

「手动校准」打开时无声，不需要麦克风。可选「开始测试音」必须在应用并连接分组后明确点击，音量不超过 25%，不会开启
麦克风。发声前先应用上限，已排队的测试音放完才解除；关闭测试窗口立即停止产生新测试音。用同一帧率、相同短距离，
分别靠近参考音箱和待校准音箱录闪光及声音；各段记录声音比闪光晚多少帧，再填
`待校准偏移 − 参考偏移`，待校准更晚为正、更早为负。助手按 `新目标微调 = 原目标微调 − 相对帧数 × 1000 / 帧率`
计算并四舍五入到毫秒，范围为 ±5000 毫秒。例如 60 fps 下偏移为 8、11 帧，相对值 +3 帧，目标微调减少 50 毫秒。
「应用微调」只改编辑器草稿，再点分组页「应用」才生效。文件播放的麦克风声学校准仍是独立可选功能；不能为了校准抢占忙碌音箱。

一份采集／音效写入共享历史，各传输读取在采集时刻加整组延迟及本台微调后应播放的帧。整组延迟容纳最慢的协商传输加
生产余量；额外等待放在历史中，而非把接收端缓冲无限加大。实时传输每包 352 帧，缓冲 AAC 每块 1024 帧；整数帧位置
避免舍入累积造成缺口。NTP 与分组 PTP 映射同一个单调时钟，每组每个本机网络接口共用一个 PTP 主时钟。

连接覆盖设置与回退顺序沿用单台规则。准备阶段不消耗实时声音，所有传输按共同的未来起点启动。必需输出失败会停止整组；
可选 Apple AirPlay、RAOP、本机和 DLNA 输出在可恢复的传输丢失后独立恢复，不重启健康输出。如果可选输出回退后的延迟超过整组延迟，它会等待重新连接分组。
忙碌、密码、配对、被接管等情况等待明确操作，自动重试不会抢回其他发送端的会话。音量偏移、静音和当前安全上限作用于
所有传输，也包含连接期间的变动。这些控制不改变 Windows 播放设备的系统音量。
DLNA 沿用现有专用网络监听和已批准的防火墙范围，不新增规则或开放公用网络。

软件测试验证共同播放时间戳、包连续性、声道、恢复、音量限制和接管行为。CI 还把生产分组的实时／缓冲路径送到固定版本
的 shairport-sync／nqptp，与独立参考逐样本比较解码 PCM，并检查请求延迟在 50 ms 内。它验证软件互通，不能证明硬件
同步精度。第三方接收端可能另加未报告的处理延迟，需用耳听或手机录像验证并微调；不需要麦克风。
GroupOutputEditorTests 通过注入调用记录检查打开时发现／采集／播放／校准精确为零，以及离线偏好库、端点／UDN 选择和
草稿校准；OutputGroupSettingsTests 覆盖命名分组和选择存储。gui-smoke 实现全部 15 种语言、必须为 150% 缩放的离线
专用场景、两种音源和无声助手，每次滚动截图都检查布局。这些软件检查不能证明真实声音边界，实机仍见维护者的
[设备清单](device-test-checklist.zh-CN.md)。
