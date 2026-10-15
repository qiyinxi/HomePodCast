# DLNA output (experimental)

P3 adds a standalone DLNA/UPnP MediaRenderer command. Group coordination and the unified device picker follow in P4/P5. Hardware compatibility has not been verified. FairPlay-only AirPlay remains unsupported; a device's separate DLNA service may work.

```text
HomePodCast.exe dlna --scan
HomePodCast.exe dlna --udn uuid:DEVICE --silent --seconds 15 --cap 10
HomePodCast.exe dlna --udn uuid:DEVICE --capture ENDPOINT_ID --volume 20 --cap 25
```

Run playback commands only when you intend to use that renderer. Select exactly one source: silence for protocol checks, or a Windows capture endpoint by ID. The command does not save settings. An active or paused renderer requires explicit `--take-over`; the sender stops if another controller replaces its URI or stops playback, and does not stop a replacement stream when closing.

The sender discovers SSDP MediaRenderer advertisements and resolves their descriptions and services on the replying IPv4 peer. It negotiates 44.1 kHz stereo LPCM (`audio/L16`, signed 16-bit big endian), then WAV (16-bit little endian). `--format l16|wav` chooses an advertised format. Compressed formats follow in P4. HTTP uses chunking by default; `--large-length` selects a finite large Content-Length for renderers that require it. WAV's RIFF lengths are finite too: these sessions cannot run indefinitely. Reconnect before the advertised response ends. Seeking is unavailable.

The session serves HTTP only on the selected local interface, while Windows reports that interface's network as Private. Only the selected renderer's IP can fetch the random session URL. A profile change closes the listener. Existing MSI/portable Private + LocalSubnet rules and MSIX Private TCP rules already cover this endpoint; P3 adds no rule, Public access or HTTP.sys URL reservation. Custom firewall rules may still block it. The command never changes network or firewall settings.

Volume is capped before starting playback. RenderingControl volume/mute are used where supported; explicitly unsupported actions fall back to digital attenuation/mute. Control or authentication failures stop startup rather than silently bypassing the error. DLNA receiver buffering and its clock vary: promise only rough alignment, suitable for different rooms. HTTP-to-filesink timing in CI measures a software renderer, not acoustic latency.

Verification uses loopback fake renderers and a pinned gmrender-resurrect/GStreamer filesink in isolated Linux CI namespaces. No sound device is used. The `dlna-compat-e2e` job checks discovery, SOAP control, both HTTP formats and sample-exact stereo PCM. Real-device testing belongs to the maintainer; keep volume at or below 25% and use manual trim or phone-video calibration when group support arrives.

## 简体中文

# DLNA 输出（实验性）

P3 提供独立的 DLNA/UPnP MediaRenderer 命令；分组协调和统一设备选择器在 P4/P5 接入。尚未验证真实设备兼容性。只认 FairPlay 的 AirPlay 设备仍不支持，但其独立的 DLNA 服务可能可用。

```text
HomePodCast.exe dlna --scan
HomePodCast.exe dlna --udn uuid:DEVICE --silent --seconds 15 --cap 10
HomePodCast.exe dlna --udn uuid:DEVICE --capture ENDPOINT_ID --volume 20 --cap 25
```

只有准备使用该设备时才运行播放命令。必须选择一种音源：协议检查用静音，或按 ID 指定 Windows 采集端点。命令不保存设置。正在播放或暂停的设备需要明确加上 `--take-over`；其他控制器更换媒体 URI 或停止播放后，本次发送停止，关闭时不会停止后来接管的流。

发送端通过 SSDP 发现 MediaRenderer，并且只向应答设备的 IPv4 地址读取描述和服务。格式优先为 44.1 kHz 双声道 LPCM（`audio/L16`，16 位有符号大端序），其次是 WAV（16 位小端序）。`--format l16|wav` 可以指定设备声明支持的格式。压缩格式留到 P4。默认使用 HTTP 分块传输；需要较大 Content-Length 的设备可加 `--large-length`。WAV 的 RIFF 长度同样有限，因此会话不能无限持续，达到声明长度前需要重新连接。不支持跳转。

HTTP 只绑定选定的本机接口，并要求 Windows 将该接口的网络识别为 Private。只有选定设备的 IP 能读取随机会话地址；网络类型改变后关闭监听。现有 MSI、便携版的 Private + LocalSubnet 规则和 MSIX 的 Private TCP 规则已经覆盖该端点；P3 不新增规则、不开放 Public 网络，也不创建 HTTP.sys URL 预留。自定义防火墙规则仍可能阻止连接。命令不会修改网络或防火墙设置。

播放前先应用音量上限。设备支持时使用 RenderingControl 音量和静音；只有明确不支持相应操作时才改用数字衰减或静音。控制或认证失败会停止连接，不会默默绕过错误。DLNA 设备的预缓冲和时钟各异，只承诺大致对齐，适合不同房间。CI 的 HTTP 到文件输出时间只是软件渲染器的测量结果，不代表实际声音延迟。

自动验证使用回环假设备，以及隔离 Linux CI 网络命名空间内固定版本的 gmrender-resurrect/GStreamer 文件输出，不使用声音设备。`dlna-compat-e2e` 检查发现、SOAP 控制、两种 HTTP 格式和逐样本一致的双声道 PCM。真实设备由维护者测试，音量不超过 25%；后续分组支持可用手动微调或手机录像校准。
