# Wireless cinema / 无线影院（experimental / 实验性）

[English](#english) | [简体中文](#简体中文)

## English

**Experimental, and never tested with real speakers.** The maintainer has one HomePod; everything below was checked
only against simulated speakers and generated files. The label stays until it has been tried with several HomePods.

The **Multi-speaker** page combines live PC capture and the **audio track** of a local media file. Choose a source,
then assign each speaker its position, 0–500 ms extra delay and volume offset. Live capture is downmixed to stereo
and offers full stereo, left and right; a 5.1/7.1 file also offers center, LFE, rear and side surrounds. It shows no
video and does not control a video player. Opening the page starts nothing. The single-speaker live path retains
its low latency; per-output positions, delay and volume offsets become available when another output is added.

### Speakers and channels

Choose at least one full-stereo output, or a left and a right HomePod; every other position is optional, up to eight
outputs. Multiple full-stereo outputs each receive the complete stereo downmix. Discrete positions require individual
HomePods, outside a stereo pair: one receiver type gives the channels the same latency and clock. Apple TV and stereo
pairs can use full stereo. Third-party receivers and AirPlay 1 compatibility receivers cannot join groups yet; the
page explains the restriction. HomePods have no subwoofer output: the LFE position is a HomePod playing only LFE.

Windows' own decoder (Media Foundation) reads the file with its speaker mask. Mono, stereo, 5.1 (with rear or side
surrounds), 6.1 and 7.1 are played; layouts with height speakers or more than 8 channels are refused with a message.
Which formats open depends on Windows: WAV and AAC in MP4/M4A always, MKV and MOV when Windows reads them, AC-3 and
E-AC-3 only where Windows has a decoder for them. Everything is resampled to 44.1 kHz.

A channel plays on the speaker of its position. A channel whose position has no speaker is folded:

| Source channel | No speaker in its position: it plays on |
| --- | --- |
| Center | front left and right, −3 dB each |
| LFE | front left and right, −6 dB each (together about the channel's own level; a theater sub's +10 dB is left out) |
| Side left/right | the rear speaker on that side, at −3 dB when the file has rear channels too (7.1), at full level when it has not (5.1); neither: the front of that side, −3 dB |
| Rear left/right | the same, with side and rear swapped |
| 5.1 surrounds, room with side **and** rear speakers | both, −3 dB each |
| Stereo, mono | front left and right only (mono is the center channel); nothing is upmixed |

So a 5.1 file labelled with rear surrounds (mask 0x3f, the usual one) and one labelled with side surrounds (0x60f) both
reach speakers set to side or rear. A 7.1 file (0x63f) on five HomePods plays its rear pair on the side speakers, and
a 5.1 file on seven plays its surrounds on both pairs.

| 7.1 file on | Front left (right alike) | Center | Side left (right alike) | Rear left (right alike) | Headroom |
| --- | --- | --- | --- | --- | --- |
| 2 HomePods | FL, C −3, LFE −6, SL −3, BL −3 | – | – | – | −11.2 dB |
| 3 HomePods | FL, LFE −6, SL −3, BL −3 | C | – | – | −9.3 dB |
| 5 HomePods (sides) | FL, LFE −6 | C | SL, BL −3 | – | −4.6 dB |
| 6 HomePods (sides, LFE) | FL | C | SL, BL −3 | – | −4.6 dB |
| 7 HomePods | FL, LFE −6 | C | SL | BL | −3.5 dB |
| 8 HomePods (with LFE) | FL | C | SL | BL | 0 dB |

Headroom: the whole mix is scaled by one fixed factor so that the busiest speaker cannot clip, which keeps the balance
between speakers as mixed and never depends on the signal. Turn the volume up to make up for it.

### Volume, delays and other connections

- The window's volume slider is the app's volume, as on Home: moving it is saved, and the cinema starts at it. A
  volume set while the start still waits in the queue is kept.
- Each speaker can wait 0 to 500 ms so that all of them reach your seat together. Layout and delays are saved
  in playback profiles too; the file path is not. Only Apply under live PC capture changes live group membership and
  positions. Selecting, playing or saving a file layout keeps the next automatic live connection unchanged.
- Playing a file ends the normal stream and the microphone. EQ, night mode and the microphone mix do not apply; the
  volume limit does. Latency is at least 200 ms. A speaker another device plays to is waited for, never taken over.
- Connecting from Home, the tray or a hotkey ends the cinema; the window then says so. The window's Stop and closing
  only ever stop the cinema's own stream, never a connection made after it.
- The log records each start (layout, what every speaker plays, volume, latency), stop, pause and calibration result,
  with speaker names marked for the diagnostic report's redaction.

### Measuring speaker delays

While playing, **Measure speaker delays** opens the chosen microphone, pauses the file and plays a 100 ms test chirp
(−20 dBFS) on one speaker after the other, **at no more than 25 % volume** whatever the volume is set to. The recording
is matched against the chirp in memory to find when each one arrived (device timestamps against the chirp's RTP time),
then cleared: nothing is saved or uploaded. The microphone closes when the measurement ends. The slower speakers get no
delay and the faster ones wait for them. A weak match, a stop, or speakers more than 500 ms apart reject the whole
measurement and keep the previous delays.

### What is tested automatically

Unit tests (`tests/HomePodCast.Tests`): the channel routing for 2.0, mono, 5.1 with either mask and 7.1 on 2, 3, 5,
6, 7 and 8 speakers and the headroom (CinemaTests); Windows decoding generated 16-bit and float WAV files with 1, 2, 6
and 8 channels and their masks (CinemaTests); one RTP timeline for every speaker, every frame in order through the
lock-free ring, pause, calibration that cannot get stuck (CinemaTransportTests); the window's volume, waiting, being
replaced by another connection, stopping and the 25 % measurement ceiling against fake speakers (CinemaSessionTests,
CinemaFormTests). Migration, separate live/file selections and per-device editor rules are covered by GroupSettingsTests
and GroupFormTests. gui-smoke visits the unified page and captures both source modes in every scenario. Nothing in the
tests plays audio or opens a microphone.

What needs real HomePods is listed in `docs/device-test-checklist.zh-CN.md` (实验性功能 → 无线影院).

## 简体中文

**实验性功能，还没有在真实音箱上测过。** 维护者只有一台 HomePod，下面的内容只用模拟音箱和生成的文件验证过。在多台
HomePod 上实测之前，这个标注不会去掉。

「多音箱」页合并电脑实时采集与本地影音文件的**音轨**。选好音源后，每台音箱可设置位置、0–500 毫秒额外延迟和音量偏移。
实时采集先混成立体声，只能选完整立体声、左、右；5.1／7.1 文件还能选中置、低音、后环绕和侧环绕。这里不显示视频，也不
控制视频播放器。打开页面不会开始任何播放。单台实时推流保留原来的低延迟路径；添加另一台后才启用每台的位置、延迟和音量偏移。

### 音箱和声道

至少选择一台完整立体声输出，或给左、右各选一台 HomePod，其他位置可选，最多八台。多台完整立体声输出都会收到完整的立体声下混。
离散位置只能用未组成立体声对的单独 HomePod：同一种接收端才能让各声道共用延迟和时钟。Apple TV 和立体声对可选完整立体声。
第三方接收端和 AirPlay 1 兼容模式设备暂不能加入分组，页面会说明原因。HomePod 没有低音炮输出：「低音」位置是一台只放 LFE 的 HomePod。

Windows 自带的解码器（Media Foundation）读取文件和它的声道掩码。能播放单声道、立体声、5.1（后环绕或侧环绕）、6.1 和
7.1；带顶部声道或超过 8 个声道的布局会提示不支持。能打开哪些格式取决于 Windows：WAV 和 MP4/M4A 里的 AAC 总能打开，
MKV、MOV 要看 Windows 能不能读，AC-3、E-AC-3 只有 Windows 装了对应解码器时才行。所有声音统一重采样到 44.1 kHz。

每个声道在它的位置上的音箱播放。位置上没有音箱时按下表混入：

| 源声道 | 它的位置没有音箱时，混入 |
| --- | --- |
| 中置 | 前左和前右，各 −3 dB |
| 低音声道（LFE） | 前左和前右，各 −6 dB（两台加起来约等于这个声道本身的电平；影院低音炮的 +10 dB 没有加） |
| 侧左／侧右 | 同一侧的后环绕音箱：文件也有后环绕声道（7.1）时 −3 dB，没有（5.1）时原电平；两个都没有就混入同一侧的前置，−3 dB |
| 后左／后右 | 同上，侧和后对调 |
| 5.1 的环绕，房间里侧环绕**和**后环绕都有 | 两对都放，各 −3 dB |
| 立体声、单声道 | 只用前左和前右（单声道就是中置声道），不做上混 |

所以标成后环绕的 5.1 文件（掩码 0x3f，最常见）和标成侧环绕的（0x60f）都能送到设成侧或后的音箱。7.1 文件（0x63f）在
五台 HomePod 上时，后环绕混进侧环绕音箱；5.1 文件在七台上时，环绕声两对都放。

| 7.1 文件放在 | 前左（前右同理） | 中置 | 侧左（侧右同理） | 后左（后右同理） | 余量 |
| --- | --- | --- | --- | --- | --- |
| 2 台 HomePod | FL、C −3、LFE −6、SL −3、BL −3 | – | – | – | −11.2 dB |
| 3 台 | FL、LFE −6、SL −3、BL −3 | C | – | – | −9.3 dB |
| 5 台（侧环绕） | FL、LFE −6 | C | SL、BL −3 | – | −4.6 dB |
| 6 台（侧环绕、低音） | FL | C | SL、BL −3 | – | −4.6 dB |
| 7 台 | FL、LFE −6 | C | SL | BL | −3.5 dB |
| 8 台（含低音） | FL | C | SL | BL | 0 dB |

余量：整个混音按一个固定系数缩小，保证混入最多的那台音箱也不会削波。这样各音箱之间的平衡保持原样，也不随信号变化。
声音小了就调大音量。

### 音量、延迟和其他连接

- 窗口里的音量滑块就是程序的音量，和首页一样：拖动后会保存，影院按它开始播放。开始播放的请求还在排队时改过的音量会保留。
- 每台音箱可以额外等待 0 到 500 毫秒，让声音同时到达座位。音箱布局和延迟会保存，也包含在使用配置里；文件路径不保存。只有在电脑实时声音下应用设置，才会更新实时组成员和位置；选择、播放或保存文件布局不会改变下一次自动实时连接。
- 播放文件会结束普通推流和麦克风。均衡、夜间模式和麦克风混音不作用于影院；音量上限照样生效。延迟至少 200 ms。音箱被其他
  设备占用时只等待，不抢占。
- 在首页、托盘或用快捷键连接会结束影院，窗口里会写明。窗口的「停止」和关闭窗口只会停止影院自己的推流，不会断开之后建立
  的连接。
- 日志记录每次开始（布局、每台音箱放什么、音量、延迟）、停止、暂停和校准结果，音箱名称做了标记，导出诊断时会被隐藏。

### 测量音箱延迟

播放时点「测量音箱延迟」：打开所选的麦克风，文件暂停，每台音箱依次放一声 100 ms 的测试音（−20 dBFS），**音量不超过
25%**，不管当前音量是多少。录音只在内存里和测试音做匹配，找出每一声到达的时间（设备时间戳对照测试音的 RTP 时间），然后
清掉，不保存也不上传。测量结束就关闭麦克风。最慢的音箱不加延迟，快的等它。匹配不可靠、中途停止或音箱之间相差超过
500 毫秒时，整次测量作废，原来的延迟不变。

### 自动测试覆盖的部分

单元测试（`tests/HomePodCast.Tests`）：2.0、单声道、两种掩码的 5.1 和 7.1 在 2、3、5、6、7、8 台音箱上的声道分配和余量
（CinemaTests）；Windows 解码生成的 16 位和浮点 WAV，1、2、6、8 声道及其掩码（CinemaTests）；所有音箱共用一条 RTP 时间线，
无锁环形缓冲里每一帧都按顺序送到，暂停，校准不会卡住（CinemaTransportTests）；窗口的音量、等待、被其他连接取代、停止，
以及测量时 25% 的上限，用模拟音箱测（CinemaSessionTests、CinemaFormTests）。GroupSettingsTests 和 GroupFormTests 覆盖配置迁移、
实时与文件选择互不影响，以及每设备编辑规则。gui-smoke 在每个场景都访问统一页并拍摄两种音源。测试不会播放声音，也不会打开麦克风。

需要真实 HomePod 才能测的部分见 `docs/device-test-checklist.zh-CN.md`（实验性功能 → 无线影院）。
