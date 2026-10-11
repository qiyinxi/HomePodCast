# Playback profiles, calibration and diagnostics

## English

### Apply your A/V measurement

Connect the speaker, pause other audio and turn the microphone off, then open **Settings → A/V sync test**. Listen to the HomePod rather than the local output. Move the flash until it coincides with the click, then select **Apply this calibration**. The test must capture and send the click successfully; if the speaker connection, capture endpoint, latency or buffer changes, start the test again.

The app saves a correction to its estimate for this speaker (or speaker group), actual capture endpoint, default/selected-device choice and per-app routing mode. Home, the browser extension and mpv/VLC all use that correction. Changing latency scenes or the adaptive buffer still changes the resulting delay. **Restore default compensation** removes only the current path's correction. **Done** only remembers the test value. If nothing could be saved, the app says why: no test sound reached the speaker stream, the connection, source or latency changed during the test, or the value is too far from the estimate.

The browser extension adds each site's own offset (its ±5/±20 ms controls) on top of this correction. If you had already compensated a site by hand, reset that site's offset in the extension's popup after applying a calibration, or the correction counts twice.

### Save a playback profile

Choose your speaker, capture device, app destinations and independent send volumes, effects, volume cap, keyboard volume mode and night mode. Open **Home → Manage profiles** or **Settings → Playback profiles**, name the profile and select **Save current settings**. Up to 32 profiles can be stored, each under its own name. Selecting a saved profile lets you rename/overwrite it with current settings or delete it.

Home's profile picker shows the profile applied last, or **Choose a profile…** until you pick one, and **Apply** restores the whole set in one action. It reconnects only when the speaker, latency or group layout changes, at most once; effects, app destinations, the capture device, the volume cap and night mode apply without interrupting the sound. A profile that switches to another speaker connects as if you had picked it in the list: while another device (an iPhone or a Mac) plays to that speaker, HomePodCast waits until it is free instead of taking it over. A profile saved before any speaker was chosen keeps the current speaker. Speaker addresses are never restored from a profile (they may have changed since): the app uses where it last saw the speaker, or finds it by its ID when connecting. The microphone is switched off on application. Profiles store a volume cap rather than playback loudness: the current saved volume can only be lowered to the cap; a different speaker uses its own volume subject to that cap. Startup, language, update preferences, passwords and calibrations remain separate. A missing capture endpoint keeps capture paused, as with a manual device selection.

### Independent app volumes

In **Mixer**, **Windows volume** and mute continue to control the Windows session. Enable **Independent HomePod volume** for a program to reveal its separate HomePod slider. For an app routed to **Both**, Windows 20% and HomePod 60% can be set independently. Enabling this feature uses per-app capture and its existing approximately 35 ms latency cost; the first activation asks for confirmation. Turn the independent switch off to follow Windows again. To return to whole-device capture, also restore all app destinations to Default and the default destination to HomePod.

Windows session mute or zero volume still silences the HomePod. Below 1% Windows volume, or when the same app's multiple audio sessions have different volumes, independent normalization temporarily falls back to scaling the Windows audio. A note on the app row explains the current limitation. The app never changes Windows volume merely to implement the independent HomePod slider.

### Report a stutter

Select **Home → Mark a stutter** when you hear one. It writes a timestamp and the current counters; this is your observation, rather than confirmation from the speaker. The last 100 markers in this run appear in the report.

**Export diagnostics** is available on Home and in Settings. Choose a local ZIP file. It contains a measurement summary and recent redacted logs (at most 2 MB per log), including the previous rotated log if available. It excludes configuration, credentials and audio. Known device/network names, addresses, identifiers and paths are masked; historical speaker connection records are removed structurally. Nothing is uploaded. Review the contents before sharing.

### Synchronized browser fullscreen

The extension shows **Sync fullscreen** on supported active video players. Click this instead of entering native video-element fullscreen; it makes the existing player container fullscreen so the delayed picture remains visible. The site's controls stay available, and exiting restores the prior layout. Browser/iframe fullscreen permission is still required.

While that player is fullscreen, use its ±5/±20 ms controls, or **Alt+Left/Right** (5 ms) and **Alt+Shift+Left/Right** (20 ms). The inline notice shows the resulting delay and saved site offset. Shortcuts ignore editable fields. Site offsets remain bounded to ±500 ms and the total video delay to 2000 ms. Native video fullscreen and picture-in-picture still cannot show the extension's delayed canvas.

## 简体中文

### 应用音画实测结果

连接音箱，暂停其他音频并关闭麦克风，打开 **设置 → 音画同步测试**。以 HomePod 发出的声音为准，移动闪光直到它和「咔」声同时出现，再点 **应用本次校准**。程序必须成功采集并送出测试音；连接、采集设备、延迟或缓冲变化后需要重新测试。

保存的是实测与估算的差值，按音箱或音箱组、实际采集设备、默认／指定设备选择和按程序分流模式隔离。首页、浏览器插件和 mpv／VLC 使用同一修正值；切换场景和自适应缓冲仍会改变总延迟。**恢复默认补偿** 只清除当前路径的校准；**完成** 只记住测试数值。没能保存时会说明原因：没有检测到送往音箱的测试音、测试期间连接／音源／延迟变了，或者结果和估算相差太大。

浏览器插件会在这个修正值之上再加各网站自己的偏移（±5／±20 ms 微调）。如果之前已经手动按网站补偿过，应用校准后请在插件弹窗里把这个网站的偏移重置，否则会补偿两次。

### 保存整套使用配置

设置好音箱、采集设备、程序去处和独立音量、音效、音量上限、键盘音量模式、夜间模式，打开 **首页 → 管理配置** 或 **设置 → 使用配置**，命名后点 **保存当前设置**。最多保存 32 个，名称不能重复。选择已有配置后可以改名并用当前设置覆盖，也可以删除。

首页的配置列表显示上次应用的配置，还没选过时显示 **选择配置…**；选好后点 **应用**，整套设置一次应用。只有音箱、延迟或多音箱布局变了才重连，最多一次；音效、程序去处、采集设备、音量上限和夜间模式直接生效，不打断声音。切换到另一只音箱的配置和在列表里选音箱一样：如果 iPhone、Mac 等其他设备正在往这只音箱推送，会等它空闲后再连，不会把它挤掉。还没选音箱时保存的配置会保留当前音箱。配置不会恢复保存时的音箱地址（之后可能变了）：用最近一次看到的地址，或者连接时按音箱 ID 查找。应用后麦克风关闭；配置保存音量上限，不保存播放音量：当前保存的音量只会降低到上限，换音箱时使用新音箱自己的音量并受上限限制。开机启动、语言、更新偏好、密码和校准单独保留。采集设备不可用时继续暂停采集，与手动选择设备的行为一致。

### 分别控制程序的两路音量

**混音器** 中的 **Windows 音量** 和静音仍控制系统音频会话。打开某个程序的 **HomePod 独立音量**，就可以使用另一个滑块。例如「两者」模式下，本机 20%、HomePod 60%。这会使用按程序分流，沿用约 35 ms 的额外延迟，首次启用会确认。关闭独立音量恢复跟随 Windows；要回到整设备采集，还需将所有程序去处改回「默认」，默认去处改为 HomePod。

Windows 音频会话静音或音量为 0 时，HomePod 也没有声音。Windows 音量低于 1%，或同一程序的多个音频会话音量不一致时，暂时按 Windows 的声音缩放推送，程序行会说明原因。调整 HomePod 独立音量本身不会修改 Windows 音量。

### 记录卡顿并导出报告

听到卡顿时点 **首页 → 刚才卡了**，记录时间和当前统计。这是用户听到的现象，不代表音箱确认发生了断音；报告保留本次运行最近 100 个标记。

首页和设置里均有 **导出诊断**，选择本机 ZIP 保存位置即可。报告包含统计摘要、最近的脱敏日志和上一份轮转日志（如有，每份最多 2 MB），不包含配置文件、密码或录音。已知设备／网络名称、地址、标识和路径会隐藏，历史音箱连接记录会按结构移除；不会上传。分享前可以检查内容。

### 浏览器同步全屏

插件会在支持的活动视频上显示 **同步全屏**。使用这个按钮，让播放器容器全屏，延迟画面和原有控制栏一起保留；退出时恢复原来的布局。浏览器或 iframe 仍需允许全屏。

全屏中可用画面上的 ±5／±20 ms 按钮，或 **Alt+左／右**（5 ms）、**Alt+Shift+左／右**（20 ms），画面会提示最终延迟和已保存的网站偏移。输入框内不会拦截按键。网站偏移范围仍为 ±500 ms，总画面延迟最多 2000 ms；视频元素原生全屏和画中画仍无法显示插件的延迟画布。
