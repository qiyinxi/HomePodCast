# Wireless cinema audio / 无线影院音频（实验性）

## English

Settings → Tools → Wireless cinema plays the discrete **audio track** of a local 5.1/7.1 file to separately assigned HomePods. It does not display video or control an external video player. Stereo sources are rejected rather than advertised as surround sound.

Native Windows Media Foundation preserves the 6/8-channel layout and speaker mask, then independent phase-aligned resamplers convert it to 44.1 kHz. Available codecs depend on Windows; an extension in the file picker is not a decoder guarantee. No extra media executable, virtual driver or cloud decoder is used. File paths are session-only; physical layouts and room alignment are saved outside playback profiles.

Up to eight independent speakers share one RTP clock, with separate encrypted sessions and resend histories. At least distinct front-left/right speakers are required. Unassigned dialogue/surround channels fold into the fronts with fixed headroom. Full discrete 5.1 needs six positions. Apple stereo pairs remain unverified for this mode.

Starting a file stops system capture and the current microphone. The system EQ, night compressor, microphone and separation chain are bypassed. Linked volume and its ceiling still apply, with the current volume (20% if unset) and at least 200 ms latency. Existing busy-speaker/home-theater policies apply. Pause and calibration preserve file position. EOF drains per-speaker delay tails and waits for AirPlay playout before teardown. Closing/stopping ends cinema and does not restart system capture.

Acoustic measurement opens the chosen mic only on request and emits sequential 100 ms chirps. Matching uses device QPC sample timestamps against RTP source deadlines, not UI polling time. Common mic delay cancels in relative offsets; faster speakers are delayed to match the slowest. Missing timestamps, weak correlation, disconnection or a spread over 500 ms rejects the complete set and retains previous settings. Transient samples are neither saved nor uploaded. Closing cancels measurement and closes capture.

Native 16-bit/float surround decode, channel isolation/folding, six encrypted payloads on one clock, pause/calibration isolation and noise/polarity correlation have automated tests. Real multi-HomePod behavior, acoustic measurement and sustained playback remain hardware checks; automated tests use synthetic files and loopback receivers without playing audio or opening the microphone.

Windows source-reader behavior: [Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/medfound/processing-media-data-with-the-source-reader).

## 简体中文

设置 → 工具 → 无线影院：选择一个含独立 5.1／7.1 音轨的本地文件，给不同 HomePod 分配前左、前右、中置、低音、后左／右、侧左／右位置，然后点击播放。这里播放文件的多声道音频，不显示视频、不控制外部视频播放器。立体声音源会拒绝播放，不会把两声道包装成环绕声。

Windows Media Foundation 在本机解码音轨，保留 6／8 声道与声道掩码，统一重采样为 44.1 kHz。支持的容器／编码取决于电脑安装的 Windows 解码器（例如 WAV、AAC/MP4；文件扩展名本身不保证可解码）。没有额外媒体程序、虚拟声卡或在线解码服务。文件路径不保存；布局和房间校准保存，但不被使用配置切换。

每个音箱使用独立的 AirPlay 会话、密钥和重传缓冲；所有声道共用 RTP 时间戳与同步时钟。最多 8 台，至少选择两台不同的前左／前右音箱。未分配的中置和环绕按固定系数混入前置，并留出幅度空间，避免丢失对白或削波。完整独立 5.1 需要六个位置。已组成 Apple 立体声对的音箱尚未验证，建议使用独立 HomePod。

播放影院文件会结束原先的系统推送，停止系统采集并关闭当前麦克风；不会把电脑上的其他声音混进去。原来的 EQ、麦克风、分轨及夜间压缩链不应用于影院文件。共享音量及音量上限继续生效。首播使用当前音量或 20%，至少 200 ms AirPlay 延迟。若音箱被其他设备占用，沿用既有等待／家庭影院接管策略。退出窗口、停止或播放结束时关闭会话；不会自动恢复系统推送。

播放／暂停只影响这里的文件音轨。暂停和校准期间保持文件位置，所有接收端继续同一时钟的静音包；结束时等待人工延迟尾部和 AirPlay 播放缓冲，避免截断最后一段声音。

点击「测量音箱延迟」时才开启所选麦克风。每个音箱依次发出约 100 ms 的小幅测试 chirp，文件临时暂停。用 WASAPI 麦克风的设备 QPC 时间戳与该测试音的 RTP 源时间比较，再用匹配相关找到抵达时间。共同的麦克风延迟在相对差值中抵消；更快的音箱增加等待，最慢的保持 0。相关置信度不足、设备时间戳不可用、断线或差异超过 500 ms 时，整套校准失败，之前的延迟不修改。只短暂使用内存样本，不保存录音或上传。关闭窗口会取消测量并关闭麦克风。

需要实机验证：多台真实 HomePod 的声道行为、时钟同步、麦克风／房间相关结果，以及长时间播放效果。代码测试覆盖 Windows 原生 16-bit/float 5.1 WAV 解码、每个声道的矩阵、六路不同加密 RTP 内容共用时间线、暂停与测试音隔离、带噪声／反相的相关和失败校准。测试没有播放到真实音箱，也没有开启真实麦克风。
