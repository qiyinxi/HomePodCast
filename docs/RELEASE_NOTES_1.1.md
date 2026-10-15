# 1.1 — Unreleased

## English

- Focused navigation pages for speaker information, multi-speaker, multichannel, synchronized viewing, A/V sync tests and usage profiles. About now contains updates, logs, diagnostics and the license; Settings contains preferences. The bottom-left theme action switches light/dark with a short fade, and the sidebar has a distinct background.

- Experimental third-party AirPlay 1 and AirPlay 2 support: combined discovery, manual address/port entry, per-device connection overrides and remembered working protocols. HomePod’s existing low-latency path and scenes retain their behavior.
- AirPlay 1 compatibility uses ALAC or L16, optional RSA/AES and Digest passwords stored locally with DPAPI. Third-party AirPlay 2 tries realtime, advertised AirPlay 1, then buffered AAC-LC over TCP with isolated PTP timing. Windows supplies the AAC encoder; no codec executable or native DLL is bundled.
- Receiver-specific latency controls: Apple TV defaults to 200 ms, third-party realtime to 250 ms, compatibility and buffered modes to 2.0 s with 1.0 / 1.5 / 2.0 s choices. Receiver processing time sets the realtime floor; below-recommendation choices warn of dropouts. AirPort Express recommends at least 2.0 s.
- Speaker info shows receiver type, active protocol and reported processing time. A one-time compatibility notice explains the delay; the A/V sync test supports these transports. The browser picture-delay limit stays at 2000 ms. Mixed-protocol/multi-speaker groups are unsupported for third-party receivers.
- Hardware status: one HomePod 2 was tested on hardware. Third-party AirPlay 2 has never been tested on hardware; classic RAOP, AirPlay 2 realtime and buffered paths are checked against shairport-sync/nqptp in CI. XGIMI Z6X was checked by handshake only and requires unsupported FairPlay; no playback compatibility is claimed. MFi-only receivers are also unsupported.
- For a supported AirPlay 1 receiver, maintainer verification still needs connection, volume/mute, another sender’s busy state and sleep/reconnect at no more than 25% volume. For the Z6X, check only the unsupported-encryption message and that automatic retries stop. Do not attempt audible playback on it.

## 简体中文

- 导航新增音箱信息、多音箱、多声道、同步观影窗口、音画同步测试及使用配置页面；关于页集中软件更新、日志、诊断与许可证，设置页保留偏好。左下角可带淡化动效切换浅深色，侧栏使用有区分的背景。

- 实验性第三方 AirPlay 1 / AirPlay 2 支持：合并发现、手动地址与端口、每台设备的连接覆盖设置及成功方式记忆。HomePod 原有低延迟路径和场景保持原行为。
- AirPlay 1 兼容模式使用 ALAC 或 L16，可选 RSA/AES 与 Digest 密码；密码经 DPAPI 保存在本机。第三方 AirPlay 2 依次尝试低延迟、已广播的 AirPlay 1、TCP 上的 AAC-LC 缓冲与独立 PTP 对时。AAC 编码器由 Windows 提供，不附带编码器程序或原生 DLL。
- 按接收端设置延迟：Apple TV 默认 200 ms，第三方低延迟默认 250 ms，兼容与缓冲默认 2.0 秒，可选 1.0 / 1.5 / 2.0 秒。接收端处理时间决定低延迟下限；低于推荐值会提示可能断音。AirPort Express 推荐至少 2.0 秒。
- 音箱信息显示设备类型、实际协议与报告的处理时间；首次兼容连接有延迟提示，音画同步测试支持这些传输。浏览器画面延迟上限仍为 2000 ms。第三方设备不支持混合协议或多音箱分组。
- 实机情况：验证过一台 HomePod 第二代。第三方 AirPlay 2 尚未实机测试；经典 RAOP、AirPlay 2 实时与缓冲路径在 CI 中用 shairport-sync/nqptp 验证。极米 Z6X 仅检查过握手，要求不支持的 FairPlay，不宣称能播放；只接受 MFi 认证的接收端也不支持。
- 可用的 AirPlay 1 接收端仍需维护者以不超过 25% 音量核对连接、音量、静音、其他发送端占用与休眠重连。极米 Z6X 只检查不支持加密的提示和自动停止重试，不尝试有声播放。
