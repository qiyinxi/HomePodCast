# Synchronized viewing window / 同步观影窗口

## English

An **experimental**, optional companion for players without a usable audio-delay setting: it shows a window you pick,
delayed by HomePodCast's current sound lag, so that the picture lines up with the HomePod. It captures no audio, opens
no website, and starts neither a player nor streaming; you control playback in the original window. The Microsoft Store
version doesn't have it.

### Use

1. Download `HomePodCast-video-window-<version>.zip` of the same version as HomePodCast from
   [Releases](https://github.com/qiyinxi/HomePodCast/releases) and extract it anywhere. It needs Windows 10 version
   2004 or later and the .NET 10 Desktop Runtime (x64). HomePodCast's updates don't update the module: download it again
   for the new version (the window says so when the two versions differ).
2. In **Synchronized viewing**, choose the module's folder with
   **Choose module…**, then click **Open**.
3. Turn off other synchronization first: the player's own audio delay, HomePodCast's "Adjust players in the Movies
   scene" and the browser extension's picture delay. With both, the picture is delayed twice.
4. **Choose window…** opens the Windows picker, where you choose a window or a whole screen; Windows keeps its yellow
   capture border around it. Cancel leaves the current capture as it is; a new source discards the old pictures. The
   mouse pointer is not captured. While it captures, this window is left out of every capture of the screen (screen
   recordings and screen sharing too), so capturing the screen it is on doesn't show the window inside itself.
5. **Follow HomePod delay** reads HomePodCast's local `/v1/status` every 500 ms: the same sound lag the browser
   extension and the players get (the AirPlay latency, capture and buffer, and the matching A/V calibration). It is 0
   while nothing streams. If the status can't be read, the last value stays and the status line says so. Clear the box
   to set a delay of 0–2000 ms yourself.
6. Fine-tune with **Sync adjustment** in 5 ms steps or ±20 ms with the buttons. **Subtract display latency
   automatically** (on by default) shows every picture that much earlier: the time from choosing a frame to seeing it
   on the screen, measured from Windows' frame statistics (one refresh of the screen until there are measurements).
   Clear it to set that value yourself.
7. F11 switches full screen on and off, Esc leaves it. **Stop capture** or closing the window frees the pictures;
   quitting HomePodCast closes the window too. Capture never starts or resumes by itself.

### How it works, and its limits

- Windows delivers every captured frame as a picture on the graphics card, stamped with the moment it appeared on the
  screen. The pictures stay on the graphics card: sources larger than 2560 × 1600 are halved until they fit
  (4K → 1920 × 1080, 5K → 2560 × 1440), and the window shows them at every refresh of the screen, at the picture's
  aspect ratio. The processor copies no pixels.
- At most 30 frames per second are kept; 30 fps video keeps all of its frames on 50 Hz, 60 Hz and faster screens. The
  queue holds 256 MiB of pictures, about a second of 1080p at 30 fps; longer delays keep fewer frames per second. A
  picture is shown when it is due, never early; when the queue is full, the oldest pictures go and the picture may
  stand still for a moment.
- SDR only: HDR video can look washed out or clipped, protected (DRM) video is black, and a minimized source may stop
  sending frames. The module doesn't get around any of this.
- The delay is a target: use the sync adjustment for what is left. Because the picture is late, the original window
  reacts late to your input: this is for watching videos, not for games. Input is not passed on to the player.
- The pictures stay in the module's memory and graphics memory; they are never saved or uploaded. HomePodCast's
  settings store only the module's folder. The status is read from the numeric loopback address only, without proxies
  or redirects, and at most 16 KiB of it.

API references: [Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture),
[DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model),
[frame latency waitable object](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject),
[SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity).

## 简体中文

这是一个**实验性**的可选模块，给没有可用音频延迟设置的播放器用：把你选的窗口按 HomePodCast 当前的声音延迟延后显示，
让画面对上 HomePod。它不采集声音，不打开网站，不启动播放器，也不开始推流；播放仍在原窗口里操作。Microsoft Store 版没有这个功能。

### 使用

1. 从 [Releases](https://github.com/qiyinxi/HomePodCast/releases) 下载和 HomePodCast 同一版本的
   `HomePodCast-video-window-<版本>.zip`，解压到任意位置。需要 Windows 10 2004 或更新版本，以及 .NET 10 桌面运行时（x64）。
   HomePodCast 更新时模块不会跟着更新，请重新下载新版本的模块（两边版本不同时，窗口里会提示）。
2. 在「同步观影窗口」用「选择模块…」选择模块所在的文件夹，然后点「打开」。
3. 先关掉其他同步补偿：播放器自己的音频延迟、HomePodCast 的「影视场景自动调整播放器」、浏览器插件的画面延迟。同时开着，画面会被延后两次。
4. 「选择窗口…」打开 Windows 的选择器，在那里选一个窗口或整块屏幕；Windows 会在它周围保留黄色的采集边框。
   取消不影响正在进行的采集；换了来源，旧画面会清掉。不采集鼠标指针。采集期间，这个窗口不出现在任何屏幕采集里
   （录屏和共享屏幕也一样），所以采集它所在的整块屏幕时，不会出现画中画的递归。
5. 「跟随 HomePod 延迟」每 500 ms 读取 HomePodCast 本机的 `/v1/status`：和浏览器插件、播放器拿到的是同一个声音延迟
   （AirPlay 延迟、采集和缓冲，以及对应的音画校准）。没在推流时是 0。读不到状态时保持上一次的值，状态栏会写出来。
   去掉勾选后，可以手动设置 0–2000 ms 的延迟。
6. 「同步微调」每次 5 ms，按钮每次 ±20 ms。「自动扣除显示延迟」（默认打开）让每一帧提前这么多显示：从选出一帧到它出现在屏幕上的时间，
   按 Windows 的帧统计实测（测到之前按屏幕刷新一次的时间算）。去掉勾选后，可以自己设置这个值。
7. F11 切换全屏，Esc 退出全屏。「停止采集」或关闭窗口会释放画面；退出 HomePodCast 也会关闭这个窗口。采集从不自动开始或恢复。

### 原理和限制

- Windows 把采集到的每一帧作为显卡上的画面交过来，并标上它出现在屏幕上的时间。画面始终留在显卡上：大于 2560 × 1600 的来源
  每次缩小一半，直到放得下（4K → 1920 × 1080，5K → 2560 × 1440）；窗口在屏幕每次刷新时显示到期的画面，保持画面比例。处理器不复制像素。
- 每秒最多保留 30 帧；30 fps 的视频在 50 Hz、60 Hz 和更高刷新率的屏幕上每一帧都保留。队列最多 256 MiB 画面，
  1080p、30 fps 时约一秒；延迟更长时每秒保留的帧会变少。画面到时间才显示，不会提前；队列满时丢掉最旧的画面，画面可能停顿一下。
- 只支持 SDR：HDR 视频可能发灰或过曝，受保护（DRM）的视频是黑的，最小化的来源窗口可能不再产生画面。模块不会绕过这些限制。
- 延迟是目标值，剩下的误差用同步微调修正。画面是延后的，原窗口对操作的反应也就显得晚：适合看视频，不适合打游戏。输入不会转发给播放器。
- 画面只在模块的内存和显存里，不保存、不上传。HomePodCast 的设置里只保存模块所在的文件夹。状态只从数字形式的本机地址读取，
  不走代理、不跟随重定向，最多读 16 KiB。

API 参考与英文部分相同。
