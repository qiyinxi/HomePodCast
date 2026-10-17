# HomePodCast 1.2 release notes — draft

## English

Live output groups are now edited on **Multi-speaker**: HomePod/Apple TV and supported third-party AirPlay 2,
AirPlay 1, exact Windows render endpoints, explicitly user-marked Bluetooth, and DLNA. The new mixed-output features
are experimental since 1.2 and have not been verified on real hardware. A single HomePod retains its existing transient
pairing, NTP, realtime PCM and scene behavior. FairPlay-only or MFi-authenticated-only AirPlay receivers remain unsupported;
the Z6X AirPlay handshake is not a playback verification, and its separate DLNA service still needs real testing.

- Add outputs by type with explicit discovery. Opening the editor or switching sources starts no local/DLNA discovery,
  capture, playback, test tone or microphone. Offline preference banks are separate from active selections.
- Set each live output's stereo/left/right channels, volume offset, required/optional behavior and ±5000 ms trim.
  Bluetooth is a user classification of a Windows endpoint, never guessed from its name. Its 200 ms extra latency is
  only a starting estimate. The capture endpoint cannot also be a local output.
- Choose automatic or manual group delay; device floors still apply and Home shows the actual delay. Save named output
  groups and select one on Home before connecting. Saving or selecting a group while idle never starts it.
- Optional Apple AirPlay, RAOP, local and DLNA outputs recover independently after recoverable failure without restarting
  healthy outputs. Authentication, busy, stopped or taken-over sessions wait for explicit action; retries never reclaim
  another sender's session. Required failure stops the group.
- Open the manual phone-video helper silently with no microphone. Record reference and target separately, enter their
  signed sound-to-flash frame difference, and apply the computed trim to the editor draft. Group **Apply** makes it
  effective. The optional explicit test tone is capped at 25% before starting; the cap stays until queued tones drain,
  and closing the test window stops new tones immediately. File-playback microphone measurement remains optional.
- Keep file groups HomePod-only, with separate file positions and 0–500 ms delays. Live selections and signed trims do
  not activate a file layout. Local/DLNA rows disappear in File mode while their live choices remain saved.
- Keep DLNA's existing Private-network and approved firewall scope. Formats include advertised LPCM, WAV, MP3 and AAC,
  with chunked or fixed-length HTTP. No Public-network access or new firewall rule is added.

The always-available **A/V sync** page combines local player delay controls with the optional synchronized viewing
window. Settings retains preferences; the viewing section keeps its experimental label and is hidden where the module
is not offered. Configuration fields and existing player behavior remain unchanged.

**Multi-speaker** adds a top-down room diagram for existing output routing, with keyboard selection, per-position
delay/volume edits and actual downmix destinations on empty positions. Clearing a position explicitly keeps its
outputs in full stereo; source changes retain saved surround assignments. The optional acoustic measurement
highlights its current speaker, and choosing a position starts no measurement. Hardware-untested labels remain.

**Diagnostics** collects the A/V sync test, local log folder and redacted diagnostic report in the former sync-test
navigation slot. Each action requires its own button click. About keeps version, software updates and license;
the existing test, log and export behavior stays the same.

Synchronization targets differ: AirPlay 2 uses a shared timeline and aims for tight synchronization; AirPlay 1 and
ordinary Bluetooth provide basic alignment after calibration; DLNA offers rough alignment for different rooms. Software
receiver PCM/marker bounds are not acoustic hardware guarantees.

The implementation includes unit coverage for selection, delay floors, named groups, silent opening, phone calibration
and independent recovery, plus focused GUI coverage for all 15 languages at required 150% scale with offline rows and
layout checks at each captured scroll. Final CI and maintainer hardware results are recorded separately before release.
See [output groups](multi-output-receivers.md), [DLNA](dlna.md), [Wireless cinema](WIRELESS_CINEMA.md) and the
[device checklist](device-test-checklist.zh-CN.md).

## 简体中文

实时输出分组已接入「多音箱」：可组合 HomePod／Apple TV、支持的第三方 AirPlay 2、AirPlay 1、完整 Windows 播放
端点、由用户明确标记的蓝牙和 DLNA。混合输出新功能从 1.2 起为实验性，尚未在真实硬件上验证。单台 HomePod 保留
原有瞬时配对、NTP、实时 PCM 和场景行为。只接受 FairPlay 或 MFi 认证发送端的 AirPlay 设备仍不支持；Z6X 的
AirPlay 握手检查不代表播放验证，独立 DLNA 服务仍需实测。

- 按类型添加输出并明确刷新。打开编辑器或切换音源不会发现本机／DLNA，也不会启动采集、播放、测试音或麦克风。
  离线偏好库与当前选择分开保存。
- 每路实时输出可设立体声／左／右、音量偏移、必需／可选及 ±5000 毫秒微调。蓝牙是用户对 Windows 端点的明确标记，
  不按名称猜测；200 毫秒附加延迟只是起始估计。本机输出不能与采集端点相同。
- 整组延迟可自动或手动，仍遵守设备下限，首页显示实际值。可保存命名输出分组，在首页选择后再连接；闲置时保存或选择
  分组不会开始连接。
- 可选 Apple AirPlay、RAOP、本机和 DLNA 在可恢复失败后独立恢复，不重启健康输出。认证、忙碌、停止或被接管的会话
  等待明确操作，重试不会抢回其他发送端；必需输出失败停止整组。
- 手机录像手动助手打开时无声、不用麦克风。分别录参考和目标音箱，填入声音相对闪光偏移的正负帧差，计算结果先应用到
  编辑器草稿，再点分组「应用」生效。可选测试音必须明确点击，发声前先应用 25% 上限，已排队的测试音放完才解除，
  关闭测试窗口立即停止产生新测试音；文件播放的麦克风测量仍是可选项。
- 文件分组仍只支持 HomePod，文件位置和 0–500 毫秒延迟独立保存。实时选择和正负微调不会启用文件布局；文件模式
  隐藏本机／DLNA 行，但保留实时选择。
- DLNA 沿用现有专用网络和已批准的防火墙范围。支持设备声明的 LPCM、WAV、MP3、AAC，以及分块或固定长度 HTTP，
  不开放公用网络、不新增防火墙规则。

常驻的「音画同步」页合并本地播放器延迟控制和可选同步观影窗口，设置页保留偏好。观影窗口一节保留实验性标签，
未提供模块时只隐藏这一节；配置字段和现有播放器行为不变。

「多音箱」新增俯视房间图，沿用已有输出路由，可用键盘选位置、调该位置的延迟和音量，空位显示实际下混去向。
清空位置会明确保留输出的完整立体声，切换音源保留已保存的环绕位置。可选声学测量会高亮当前音箱，点选位置不会
开始测量；尚未实机验证的标签保留。

「诊断」页在原音画同步测试的位置集中音画同步测试、日志文件夹和脱敏诊断报告，各自需要明确点击按钮才执行。
关于页保留版本、软件更新和许可证；原有测试、日志和导出行为不变。

同步目标按类型区分：AirPlay 2 共用时间线、目标严格同步；AirPlay 1 和普通蓝牙校准后基本对齐；DLNA 适合不同房间的
大致对齐。软件接收端的 PCM／标记边界不能当作真实声音的硬件保证。

实现包含选择、延迟下限、命名分组、无声打开、手机校准和独立恢复的单元覆盖，以及全部 15 种语言、必须为 150% 缩放
的离线行 GUI 专用覆盖，每次滚动截图都检查布局。最终 CI 和维护者实机结果在发布前另行记录。详见[输出分组](multi-output-receivers.md)、
[DLNA](dlna.md)、[无线影院](WIRELESS_CINEMA.md)及[设备清单](device-test-checklist.zh-CN.md)。
