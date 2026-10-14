# Privacy policy

[English](#english) | [简体中文](#简体中文)

## English

*Last updated: 14 October 2026. Applies to HomePodCast from GitHub (MSI installer, zip) and from the Microsoft Store, and to the HomePodCast browser extension.*

HomePodCast doesn't collect personal data. It has no accounts, no telemetry, no analytics and no ads, and it sends nothing about you or your PC to the developer or to anyone else.

**Sound.** HomePodCast captures the sound of Windows (or of the apps you pick) and streams it over your local network to the AirPlay speakers you choose, such as your HomePods. The audio goes only to those speakers. It is processed in memory and never recorded or stored. In the wireless cinema (Settings > Tools), it instead reads the audio track of a media file you pick, decodes it with Windows' own decoders and streams it the same way; the file is only read, and its path is not saved.

**Screen (synchronized viewing window).** The optional viewing window module (a separate download from GitHub, not in the Microsoft Store version) captures only the window or screen you pick in Windows' own picker, which Windows marks with a yellow border, and only until you stop the capture or close the window. The pictures stay in your PC's memory and graphics memory to be shown a little later; they are never recorded, stored or sent anywhere. It reads the current delay from HomePodCast on your own PC (`127.0.0.1`).

**Microphone.** The microphone is used in two cases only, and is always off when the app starts:

- While you have it switched on (Microphone and effects page, or the tray flyout). Your voice is then played on the HomePod and/or on a local output, whichever you chose. It is processed in memory (gain, gate, equalizer, reverb) and never recorded, stored, analysed or sent anywhere else.
- While a speaker-delay measurement you started in the wireless cinema runs (**Measure speaker delays**; a few seconds). The microphone records the short test sounds the speakers play, and the app analyses that recording in memory on your PC to find when each test sound arrived. Only the resulting delays are kept (in the settings); the recording itself is discarded right away, never stored and never sent anywhere. The microphone closes when the measurement ends.

Windows shows a microphone-in-use indicator while it is on. The Microsoft Store version also needs your permission in Windows to use the microphone, which you can withdraw at any time under Settings > Privacy & security > Microphone.

**Your local network.** To find and play to speakers, the app uses mDNS (Bonjour) discovery and AirPlay 2 on your home network. It pings the speaker and your router to warn you about network trouble. None of this leaves your network.

**What stays on your PC.**

- Settings, including the speaker you last used (its name and network address), your choices for each app and the wireless cinema's speakers (names, network addresses, delays and the chosen microphone), are kept in a settings file in your user profile (`%APPDATA%\HomePodCast`; the Microsoft Store version may keep them in the app's own storage instead, which Windows deletes when you uninstall the app).
- The app keeps a log file there too: what the app did, such as speaker names, their network addresses, audio device names and the names of apps it routes. It is there so you can attach it to a bug report if you want to; it never leaves your PC unless you send it yourself.
- If you enter the password of VLC's web interface (for syncing VLC to the speaker delay), it is stored encrypted with Windows' data protection (DPAPI) for your Windows account only.
- To keep video players in sync, the app reads the settings files of mpv and VLC and talks to VLC's web interface on your PC. It also looks at which apps are playing sound so it can route them. All of this stays on your PC.

**Internet.** HomePodCast connects to the internet only if you turn on update checks or press **Check for updates**. It then asks GitHub's servers for the latest HomePodCast release, and if you choose to update, the new version is downloaded from GitHub. As with any web request, GitHub sees your IP address ([GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)). The Microsoft Store version is updated by the Store instead. Links in the app (for example to this project's page) open in your web browser.

**Browser extension.** The extension talks only to the HomePodCast app on your own PC (`http://127.0.0.1:47100`) to learn the current audio delay, and uses it to delay the video in the page you are watching. It sends nothing anywhere else and collects nothing.

**Microsoft Store.** If you get HomePodCast from the Microsoft Store, Microsoft handles the download, installation and updates under the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement). Microsoft gives developers aggregated statistics such as install counts and crash reports through Partner Center; these don't identify you to the developer.

**Children.** HomePodCast collects no data from anyone, children included.

**Changes.** Changes to this policy are published in this file; its history is in the [repository](https://github.com/qiyinxi/HomePodCast/commits/main/PRIVACY.md).

**Contact.** Questions or concerns: open an issue at <https://github.com/qiyinxi/HomePodCast/issues>.

## 简体中文

*最后更新：2026 年 10 月 14 日。适用于从 GitHub 获取的 HomePodCast（MSI 安装包、zip）、Microsoft Store 版 HomePodCast，以及 HomePodCast 浏览器扩展。*

HomePodCast 不收集个人数据。它没有账号、没有遥测、没有统计分析、没有广告，不会把任何关于你或你电脑的信息发送给开发者或其他任何人。

**声音。** HomePodCast 采集 Windows 的声音（或你选定的应用的声音），通过本地网络串流到你选择的 AirPlay 音箱（例如 HomePod）。音频只发往这些音箱，只在内存中处理，从不录制或保存。在无线影院（设置 > 工具）里，它改为读取你选择的影音文件的音轨，用 Windows 自带的解码器解码，再同样地推送；文件只被读取，文件路径不会保存。

**画面（同步观影窗口）。** 可选的同步观影窗口模块（从 GitHub 单独下载，Microsoft Store 版没有）只采集你在 Windows 自己的选择器里选的那个窗口或屏幕，Windows 会用黄色边框标出它；你停止采集或关闭窗口后就不再采集。画面只在你电脑的内存和显存里，稍后显示出来，从不录制、保存或发往任何地方。它从你电脑上的 HomePodCast（`127.0.0.1`）读取当前的延迟。

**麦克风。** 只在下面两种情况下使用麦克风；应用每次启动时麦克风都是关闭的：

- 你打开麦克风开关时（“麦克风与音效”页面或托盘弹出面板）。你的声音按你的选择在 HomePod 和/或本机输出设备上播放，只在内存中处理（增益、噪声门、均衡器、混响），从不录制、保存、分析，也不会发送到其他任何地方。
- 你在无线影院里开始的音箱延迟测量进行期间（“测量音箱延迟”，几秒钟）。麦克风录下音箱发出的短促测试音，应用在你的电脑上、在内存中分析这段录音，找出每个测试音到达的时间。只保留算出的延迟（存在设置里）；录音本身随即丢弃，从不保存，也不会发送到任何地方。测量结束时麦克风就关闭。

麦克风开启期间，Windows 会显示麦克风使用中的提示。Microsoft Store 版使用麦克风还需要你在 Windows 中授予权限，你可以随时在“设置 > 隐私和安全性 > 麦克风”中撤销。

**本地网络。** 为了发现音箱并向其播放，应用在你的家庭网络中使用 mDNS（Bonjour）发现和 AirPlay 2，并会 ping 音箱和路由器，以便提示网络问题。这些都不会离开你的网络。

**只保存在你电脑上的内容。**

- 设置（包括上次使用的音箱的名称和网络地址、你为各个应用做的选择，以及无线影院的音箱名称、网络地址、延迟和所选的麦克风）保存在你用户目录下的设置文件中（`%APPDATA%\HomePodCast`；Microsoft Store 版可能改为保存在应用自己的存储中，卸载应用时由 Windows 删除）。
- 同一位置还有一个日志文件，记录应用做了什么，例如音箱名称及其网络地址、音频设备名称和被路由的应用名称。它的用途是让你在反馈问题时可以附上；除非你自己发送，它不会离开你的电脑。
- 如果你填写了 VLC 网页界面的密码（用于让 VLC 跟随音箱延迟同步），它会用 Windows 数据保护（DPAPI）加密保存，只有你的 Windows 账户能解密。
- 为了让视频播放器保持同步，应用会读取 mpv 和 VLC 的设置文件，并与你电脑上的 VLC 网页界面通信；为了按应用分配声音，它会查看哪些应用正在播放声音。这些都只在你的电脑上进行。

**互联网。** 只有当你打开更新检查或点击 **检查更新** 时，HomePodCast 才会连接互联网：它向 GitHub 的服务器查询 HomePodCast 的最新版本；如果你选择更新，新版本从 GitHub 下载。与任何网络请求一样，GitHub 会看到你的 IP 地址（见 [GitHub 隐私声明](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)）。Microsoft Store 版由 Store 负责更新。应用中的链接（例如本项目主页）会在你的浏览器中打开。

**浏览器扩展。** 扩展只与你本机上的 HomePodCast 应用通信（`http://127.0.0.1:47100`），获取当前的音频延迟，用来让你正在观看的网页视频同步延迟。它不向其他任何地方发送数据，也不收集任何数据。

**Microsoft Store。** 如果你从 Microsoft Store 获取 HomePodCast，下载、安装和更新由 Microsoft 依照 [Microsoft 隐私声明](https://privacy.microsoft.com/privacystatement) 处理。Microsoft 会通过合作伙伴中心向开发者提供汇总统计，例如安装数量和崩溃报告；这些信息不会让开发者识别出你本人。

**儿童。** HomePodCast 不收集任何人的数据，包括儿童。

**变更。** 本政策的变更会发布在此文件中，修改历史见[代码仓库](https://github.com/qiyinxi/HomePodCast/commits/main/PRIVACY.md)。

**联系方式。** 如有问题或疑虑，请在 <https://github.com/qiyinxi/HomePodCast/issues> 提交 issue。
