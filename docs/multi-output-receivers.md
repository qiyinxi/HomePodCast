# Coordinated AirPlay receivers / AirPlay 接收端分组

## English

Experimental since 1.2; third-party groups have not been tested on hardware. Live PC audio can combine HomePods,
third-party AirPlay 2 realtime or buffered receivers, AirPlay 1, configured local outputs and DLNA. The unified
local/DLNA picker follows in P5. A single HomePod retains its original low-latency path. Local-file groups still
require HomePods. FairPlay-only devices remain unsupported; their separate DLNA service may work.

One capture/effects producer writes the shared history. Every transport reads the frames due at capture time plus
the group delay and its saved trim. The group delay accommodates the slowest negotiated transport plus producer
headroom; extra delay is stored in history rather than an oversized receiver buffer. Realtime transports use
352-frame packets; buffered AAC uses 1024-frame blocks. Integer frame positions prevent cumulative rounding gaps.
NTP and group PTP project the same monotonic clock, with one shared PTP master per local interface and group.

The protocol override and fallback order match standalone receivers. Preparation does not consume live audio.
All transports start on a common future epoch. Required AirPlay 2 outputs stop the group on loss; an optional
output reconnects independently after fresh discovery. A slower optional fallback pauses until the group is
reconnected with sufficient delay. Busy, password, pairing and takeover failures wait for explicit action;
automatic retries do not reclaim another sender's session. Volume offsets, mute and the current safety cap apply
to every transport, including changes made while connecting. These controls do not change Windows endpoint volume.

Software tests verify shared presentation timestamps, packet continuity, channel mapping, recovery, caps and
takeover behavior. CI additionally sends the production coordinated realtime/buffered paths to pinned
shairport-sync/nqptp, compares decoded PCM with an independent reference and checks requested delay within 50 ms.
This establishes software interoperability, not a measured hardware synchronization guarantee. Third-party
receivers can add undisclosed processing, so validate and trim by ear or phone video; no microphone is required.

## 简体中文

1.2 起为实验性；第三方分组尚未在真实设备上测试。电脑实时声音可以混用 HomePod、第三方 AirPlay 2 实时或缓冲接收端、
AirPlay 1、已配置的本机输出和 DLNA。本机／DLNA 统一选择器在 P5 接入。单台 HomePod 保留原有低延迟路径。
本地文件分组仍只支持 HomePod。只认 FairPlay 的设备始终不支持，但它独立提供的 DLNA 服务可能可用。

一份采集／音效写入共享历史，各传输读取在采集时刻加整组延迟及本台微调后应播放的帧。整组延迟容纳最慢的协商传输加
生产余量；额外等待放在历史中，而非把接收端缓冲无限加大。实时传输每包 352 帧，缓冲 AAC 每块 1024 帧；整数帧位置
避免舍入累积造成缺口。NTP 与分组 PTP 映射同一个单调时钟，每组每个本机网络接口共用一个 PTP 主时钟。

连接覆盖设置与回退顺序沿用单台规则。准备阶段不消耗实时声音，所有传输按共同的未来起点启动。必需的 AirPlay 2 输出
掉线会停止整组；可选输出重新发现后独立恢复。如果可选输出回退后的延迟超过整组延迟，它会等待重新连接分组。
忙碌、密码、配对、被接管等情况等待明确操作，自动重试不会抢回其他发送端的会话。音量偏移、静音和当前安全上限作用于
所有传输，也包含连接期间的变动。这些控制不改变 Windows 播放设备的系统音量。

软件测试验证共同播放时间戳、包连续性、声道、恢复、音量限制和接管行为。CI 还把生产分组的实时／缓冲路径送到固定版本
的 shairport-sync／nqptp，与独立参考逐样本比较解码 PCM，并检查请求延迟在 50 ms 内。它验证软件互通，不能证明硬件
同步精度。第三方接收端可能另加未报告的处理延迟，需用耳听或手机录像验证并微调；不需要麦克风。
