# Synchronized viewing window / 同步观影窗口

## English

An **experimental**, optional Windows Graphics Capture companion for players without a usable audio-delay API.
It delays a selected window's picture by HomePodCast's current audio lag. It captures no audio and does not open
a website, start a player, or start HomePod streaming. Control playback in the original window.

### Use

1. Extract `HomePodCast-video-window-<version>.zip` from the same build. Windows 10 version 2004 or later and
   .NET 10 Desktop Runtime x64 are required. The main app keeps its existing target and dependencies.
2. In Settings → Tools → **Synchronized viewing window (experimental)**, choose the module folder and open it.
3. Turn off other synchronization compensation first: the player's audio-delay correction, HomePodCast's Movies
   player-sync option, and any browser extension picture compensation. Applying both compensates twice.
   Stream the player's sound normally through HomePodCast.
4. Click **Choose window…** in the companion. Windows' picker lets you explicitly select a window or screen and
   retains the normal capture border. For screen capture, keep the companion outside that screen to avoid recursion.
   Cancel leaves the previous capture alone; changing source discards old pictures.
5. **Follow HomePod delay** reads the main app's read-only loopback `/v1/status` every 500 ms: negotiated AirPlay
   delay, capture/FIFO contribution and applicable A/V calibration. It uses the actual bound API port, including
   configurations requesting an ephemeral port. No external service or account is involved.
6. Adjust by 5 ms with the numeric control or ±20 ms with the buttons. Uncheck following for a manual 0–2000 ms
   delay. When streaming stops the automatic base becomes zero; unavailable status keeps the last known delay
   with a visible indication. F11 toggles fullscreen; Esc leaves it. Stop capture or close the companion to release
   resources and pictures. Quitting the main app also stops its companion. Capture never resumes automatically.

### Limits and verification

- First implementation: SDR preview up to 1280 × 720 and 30 fps, D3D11 readback and WinForms drawing.
  It is **not** a zero-copy GPU renderer or an HDR pipeline. HDR can clip; protected/DRM content can be black.
  Minimized or suspended source windows can stop producing frames. The module cannot bypass these restrictions.
- The picture queue is capped at 128 MiB and 64 frames. Native capture/staging buffers, temporary source bitmaps
  and the displayed frame use additional memory. Long delays reduce capture cadence to fit about 30 preview frames.
  Input above 8,847,360 pixels is rejected to avoid unbounded allocations.
- Frames carry Windows' QPC capture timestamps; only due frames can leave the queue. Capacity pressure discards
  old frames and may freeze the preview; it never displays an early replacement. Resize recreates the native pool
  and clears queued pictures. Source closure stops capture and clears the preview.
- Refresh, readback and paint scheduling still contribute visual error. The displayed delay is a target, not a
  measured presentation timestamp; use fine adjustment for residual error. Added input-to-picture delay makes
  this suitable for watching video rather than latency-sensitive gaming. Input is not forwarded to the player.
- Pictures stay in process memory, are not saved or uploaded. Only the module path is persisted. Status polling
  uses numeric loopback without proxies or redirects and caps responses at 16 KiB.
- Tests cover due-frame selection, delay changes, byte/frame limits, disposal, status validation, literal launcher
  arguments and translations. A synthetic WARP texture verifies WinRT surface → D3D11 staging → BGRA bitmap
  without opening a window or capturing the desktop. CI builds and packages the module. Actual capture, picker/
  fullscreen layout, protected content and real hardware A/V alignment remain manual checks. No desktop page,
  real player or speaker was launched for this implementation.

API references: [Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture),
[CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded),
[Direct3D surface interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-createdirect3d11surfacefromdxgisurface).

## 简体中文

这是一个**实验性**的可选 Windows Graphics Capture 模块，适用于没有可用音频延迟接口的播放器。
它按照 HomePodCast 当前的声音延迟，延后显示选定窗口。它不采集音频，不打开网站、不启动播放器，
也不自动开始 HomePod 推流。播放操作仍在原窗口进行。

### 使用

1. 解压同一构建的 `HomePodCast-video-window-<版本>.zip`。需要 Windows 10 2004 或更新版本，
   以及 .NET 10 Desktop Runtime x64；主程序的目标平台和依赖保持原样。
2. 在「设置 → 工具 → 同步观影窗口（实验性）」选择模块文件夹，然后打开。
3. 先关闭其他同步补偿：播放器的音频提前、HomePodCast 的「影视场景自动调整播放器」、
   浏览器扩展的画面补偿；同时开启会重复调整。通过 HomePodCast 正常推流播放器的声音。
4. 在模块里点「选择窗口…」。Windows 选择器让你明确选择窗口或屏幕，保留系统采集边框。
   采集屏幕时，把模块放在另一块屏幕，避免画面递归。取消不影响原采集；换源清空旧画面。
5. 「跟随 HomePod 延迟」每 500 ms 读取主程序只读的本机 `/v1/status`，取得协商的 AirPlay 延迟、
   采集和 FIFO 延迟及适用的音画校准。使用实际绑定的 API 端口，也支持随机端口，不涉及外部服务或账号。
6. 数字框每次微调 5 ms，按钮每次 ±20 ms。关闭跟随后，可手动设置 0–2000 ms 延迟。
   停止推流时，自动延迟基数归零；状态不可用时保留最后一次延迟，并显示提示。
   F11 切换全屏，Esc 退出。停止采集或关闭模块会释放资源和画面；退出主程序也会停止它启动的模块。
   不会自动恢复采集。

### 限制与验证

- 首版最高 1280 × 720、30 fps SDR 预览，通过 D3D11 读回和 WinForms 绘图，
  **不是**零拷贝 GPU 渲染器或 HDR 管线。HDR 可能过曝，受保护或 DRM 内容可能黑屏。
  最小化或暂停运行的原窗口可能不再产生画面，模块不能绕过这些限制。
- 队列最多 128 MiB、64 帧；原生采集和暂存纹理、临时原图、正在显示的帧另占内存。
  延迟较长时降低采集帧率，以容纳约 30 帧预览。超过 8,847,360 像素的输入被拒绝，避免无限分配。
- 使用 Windows QPC 采集时间戳，只显示到期帧。容量不足时丢弃旧帧，预览可能暂时停住，
  不会提前显示新帧。改变尺寸时重建采集池并清空队列；原窗口关闭时停止采集并清空预览。
- 屏幕刷新、像素读回和绘制调度仍有误差；显示的延迟是目标值，不是实测呈现时间，可用微调修正。
  模式增加操作到画面的延迟，适合看视频，不适合低延迟游戏，也不把输入转发给播放器。
- 画面只留在进程内存，不保存、不上传；只持久化模块路径。状态读取限于数字形式本机地址，
  不使用代理、不跟随重定向，响应最多 16 KiB。
- 测试覆盖到期帧、延迟变化、内存和帧数上限、释放、状态校验、字面启动参数和翻译。
  合成 WARP 纹理验证 WinRT 表面 → D3D11 暂存 → BGRA 位图路径，不开窗口、不采集桌面；CI 构建并打包。
  实际采集、选择器和全屏布局、受保护内容及真实硬件音画对齐仍需人工验证。
  本次没有打开桌面页面、真实播放器或连接真实音箱。API 参考与英文部分相同。
