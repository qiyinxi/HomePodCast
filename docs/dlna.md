# DLNA output (experimental)

Standalone DLNA/UPnP MediaRenderer playback is available through the command below. Coordinated groups are available through the API and per-UDN settings; the unified device picker follows later. Hardware compatibility has not been verified. FairPlay-only AirPlay remains unsupported; a device's separate DLNA service may work.

```text
HomePodCast.exe dlna --scan
HomePodCast.exe dlna --udn uuid:DEVICE --silent --seconds 15 --cap 10
HomePodCast.exe dlna --udn uuid:DEVICE --capture ENDPOINT_ID --volume 20 --cap 25
```

Run playback commands only when you intend to use that renderer. Select exactly one source: silence for protocol checks, or a Windows capture endpoint by ID. The command does not save settings. An active or paused renderer requires explicit `--take-over`; the sender stops if another controller replaces its URI or stops playback, and does not stop a replacement stream when closing.

The sender discovers SSDP MediaRenderer advertisements and resolves their descriptions and services on the replying IPv4 peer. It prefers 44.1 kHz stereo LPCM (`audio/L16`, signed 16-bit big endian), then WAV (16-bit little endian), MP3, and AAC-LC in ADTS. Compressed formats use Windows Media Foundation. `--format l16|wav|mp3|aac` chooses an advertised format. HTTP uses chunking by default; `--large-length` selects a finite large Content-Length for renderers that require it. WAV's RIFF lengths are finite too: these sessions cannot run indefinitely. Reconnect before the advertised response ends. Seeking is unavailable.

The session serves HTTP only on the selected local interface, while Windows reports that interface's network as Private. Only the selected renderer's IP can fetch the random session URL. A profile change closes the listener. Existing MSI/portable Private + LocalSubnet rules and MSIX Private TCP rules already cover this endpoint; P3 adds no rule, Public access or HTTP.sys URL reservation. Custom firewall rules may still block it. The command never changes network or firewall settings.

Volume is capped before starting playback. RenderingControl volume/mute are used where supported; explicitly unsupported actions fall back to digital attenuation/mute. Control or authentication failures stop startup rather than silently bypassing the error. DLNA receiver buffering and its clock vary: promise only rough alignment, suitable for different rooms. HTTP-to-filesink timing in CI measures a software renderer, not acoustic latency.

Groups resolve each saved UDN afresh, rather than trusting a saved address. A DLNA output is optional by default and reconnects independently after transport failure. A required output ends the group on failure. Busy, unsupported, stopped or taken-over outputs wait for explicit action; automatic retries do not reclaim another controller's stream. The existing single-HomePod path is unchanged.

Each output has an estimated receiver latency, signed trim, channel assignment and volume offset. Positive trim delays that output further. The shared delay covers the estimates, negative trims, a 250 ms priming margin and the common producer's headroom. This aligns outputs against the shared processed history; it does not measure the earlier capture/FIFO delay or an acoustic arrival time. Use manual or phone-video calibration for the real receiver offset.

Receiver-paced HTTP corrects drift only after a stable backpressured read-rate window becomes observable. Startup bursts and sender-paced writes cannot establish a receiver clock. The fit needs 45 seconds, keeps at most 60 seconds of evidence, and expires after a two-second observation gap. Long stalls, history wrap and a new HTTP GET re-anchor the reader. Clock confidence describes this transport proxy, not acoustic accuracy. Four fake-clock hour simulations at ±300/±1000 ppm remain within 500 ms under their tested conditions; real hardware has no verified bound. DLNA still promises rough alignment for different rooms.

For an API/configuration test, save preferences and an explicit live selection separately. This example selects only DLNA; the empty AirPlay selection excludes even a remembered stereo pair. Saving the preference bank alone never connects anything. Omit the empty AirPlay selection to retain the current ordinary AirPlay primary, or select its saved IDs explicitly. Do not include discovery addresses or service URLs.

```json
{
  "GroupDlnaOutputs": [
    { "Udn": "uuid:DEVICE", "Name": "Living room", "EstimatedLatencyMs": 2000, "TrimMs": 0, "Required": false }
  ],
  "GroupCaptureDlnaUdns": ["uuid:DEVICE"],
  "GroupCaptureDeviceIds": []
}
```

Verification uses loopback fake renderers and a pinned gmrender-resurrect/GStreamer filesink in isolated Linux CI namespaces. No sound device is used. `dlna-compat-e2e` checks discovery, SOAP, sample-exact LPCM/WAV, and MP3/AAC with both HTTP framings. Windows CI generates the real encoded fixtures; Linux independently decodes them and compares aligned renderer PCM with decoder-rounding tolerance. Real-device testing belongs to the maintainer; keep volume at or below 25% and calibrate with manual trim or phone video.

## 简体中文

# DLNA 输出（实验性）

下方命令提供独立的 DLNA/UPnP MediaRenderer 播放；分组协调已提供 API 和按 UDN 保存的配置，统一设备选择器后续接入。尚未验证真实设备兼容性。只认 FairPlay 的 AirPlay 设备仍不支持，但其独立的 DLNA 服务可能可用。

```text
HomePodCast.exe dlna --scan
HomePodCast.exe dlna --udn uuid:DEVICE --silent --seconds 15 --cap 10
HomePodCast.exe dlna --udn uuid:DEVICE --capture ENDPOINT_ID --volume 20 --cap 25
```

只有准备使用该设备时才运行播放命令。必须选择一种音源：协议检查用静音，或按 ID 指定 Windows 采集端点。命令不保存设置。正在播放或暂停的设备需要明确加上 `--take-over`；其他控制器更换媒体 URI 或停止播放后，本次发送停止，关闭时不会停止后来接管的流。

发送端通过 SSDP 发现 MediaRenderer，并且只向应答设备的 IPv4 地址读取描述和服务。格式优先为 44.1 kHz 双声道 LPCM（`audio/L16`，16 位有符号大端序），其次是 WAV（16 位小端序）、MP3、ADTS 封装的 AAC-LC。压缩格式使用 Windows Media Foundation。`--format l16|wav|mp3|aac` 可以指定设备声明支持的格式。默认使用 HTTP 分块传输；需要较大 Content-Length 的设备可加 `--large-length`。WAV 的 RIFF 长度同样有限，因此会话不能无限持续，达到声明长度前需要重新连接。不支持跳转。

HTTP 只绑定选定的本机接口，并要求 Windows 将该接口的网络识别为 Private。只有选定设备的 IP 能读取随机会话地址；网络类型改变后关闭监听。现有 MSI、便携版的 Private + LocalSubnet 规则和 MSIX 的 Private TCP 规则已经覆盖该端点；P3 不新增规则、不开放 Public 网络，也不创建 HTTP.sys URL 预留。自定义防火墙规则仍可能阻止连接。命令不会修改网络或防火墙设置。

播放前先应用音量上限。设备支持时使用 RenderingControl 音量和静音；只有明确不支持相应操作时才改用数字衰减或静音。控制或认证失败会停止连接，不会默默绕过错误。DLNA 设备的预缓冲和时钟各异，只承诺大致对齐，适合不同房间。CI 的 HTTP 到文件输出时间只是软件渲染器的测量结果，不代表实际声音延迟。

分组每次都按保存的 UDN 重新发现设备，不信任保存的地址。DLNA 输出默认为可选，传输失败后独立重连；必需输出失败会结束整组。忙碌、不支持、停止播放或被接管的设备等待明确操作，自动重试不会抢回其他控制器的流。原有单台 HomePod 路径不变。

每台可设置估计的接收端延迟、有正负值的微调、声道和音量偏移。正微调让该输出更晚播放。整组延迟覆盖各台估计值、负微调、250 毫秒启动余量和公共生产者的余量。这是在共用的效果处理后历史上对齐，不会测出更早的采集／FIFO 延迟或实际声音到达时间。真实设备的偏移需手动或用手机录像校准。

由接收端控制的 HTTP 流只有在稳定背压使读取速率可观测后才校正漂移；启动突发和发送端限速不能证明设备时钟。拟合至少需要 45 秒，最多保留 60 秒证据，两秒没有有效观测就失效。长时间停顿、历史覆盖和新的 HTTP GET 都会重新定位读取器。时钟置信度只描述传输代理，不代表声学精度。正负 300／1000 ppm 的四组假时钟小时仿真在测试条件下偏差小于 500 毫秒，真实硬件尚无经过验证的上限。DLNA 仍只承诺大致对齐，适合不同房间。

API／配置测试需要分别保存偏好和明确的实时选择。下例只选择 DLNA；空的 AirPlay 选择也会排除记住的立体声对。仅保存偏好库不会连接设备。要保留当前普通 AirPlay 主音箱，可省略空的 AirPlay 选择，或明确选择保存的 ID。不要保存发现时的地址和服务 URL。

```json
{
  "GroupDlnaOutputs": [
    { "Udn": "uuid:DEVICE", "Name": "客厅", "EstimatedLatencyMs": 2000, "TrimMs": 0, "Required": false }
  ],
  "GroupCaptureDlnaUdns": ["uuid:DEVICE"],
  "GroupCaptureDeviceIds": []
}
```

自动验证使用回环假设备，以及隔离 Linux CI 网络命名空间内固定版本的 gmrender-resurrect/GStreamer 文件输出，不使用声音设备。`dlna-compat-e2e` 检查发现、SOAP、逐样本一致的 LPCM／WAV，以及两种 HTTP 传输方式的 MP3／AAC。Windows CI 生成真实编码样本，Linux 独立解码，并在对齐后以解码器舍入容差比较设备输出 PCM。真实设备由维护者测试，音量不超过 25%，使用手动微调或手机录像校准。
