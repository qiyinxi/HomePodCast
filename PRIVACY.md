# Privacy policy

[English](#english) | [简体中文](#简体中文)

## English

*Last updated: 11 October 2026. Applies to HomePodCast from GitHub (MSI installer, zip) and from the Microsoft Store, and to the HomePodCast browser extension.*

HomePodCast doesn't collect personal data. It has no accounts, no telemetry, no analytics and no ads, and it sends nothing about you or your PC to the developer or to anyone else.

**Sound.** HomePodCast captures the sound of Windows (or of the apps you pick) and streams it over your local network to the AirPlay speakers you choose, such as your HomePods. The audio goes only to those speakers. It is processed in memory and never recorded or stored.

**Microphone.** The microphone is used only while you have it switched on (Microphone and effects page, or the tray flyout). It is always off when the app starts. Your voice is then played on the HomePod and/or on a local output, whichever you chose. It is processed in memory (gain, gate, equalizer, reverb) and never recorded, stored, analysed or sent anywhere else. Windows shows a microphone-in-use indicator while it is on. The Microsoft Store version also needs your permission in Windows to use the microphone, which you can withdraw at any time under Settings > Privacy & security > Microphone.

**Your local network.** To find and play to speakers, the app uses mDNS (Bonjour) discovery and AirPlay 2 on your home network. It pings the speaker and your router to warn you about network trouble. None of this leaves your network.

**What stays on your PC.**

- Settings, including the speaker you last used (its name and network address) and your choices for each app, are kept in a settings file in your user profile (`%APPDATA%\HomePodCast`; the Microsoft Store version may keep them in the app's own storage instead, which Windows deletes when you uninstall the app).
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

*最后更新：2026 年 10 月 11 日。适用于从 GitHub 获取的 HomePodCast（MSI 安装包、zip）、Microsoft Store 版 HomePodCast，以及 HomePodCast 浏览器扩展。*

HomePodCast 不收集个人数据。它没有账号、没有遥测、没有统计分析、没有广告，不会把任何关于你或你电脑的信息发送给开发者或其他任何人。

**声音。** HomePodCast 采集 Windows 的声音（或你选定的应用的声音），通过本地网络串流到你选择的 AirPlay 音箱（例如 HomePod）。音频只发往这些音箱，只在内存中处理，从不录制或保存。

**麦克风。** 只有在你打开麦克风开关时（“麦克风与音效”页面或托盘弹出面板）才会使用麦克风；应用每次启动时麦克风都是关闭的。打开后，你的声音按你的选择在 HomePod 和/或本机输出设备上播放，只在内存中处理（增益、噪声门、均衡器、混响），从不录制、保存、分析，也不会发送到其他任何地方。麦克风开启期间，Windows 会显示麦克风使用中的提示。Microsoft Store 版使用麦克风还需要你在 Windows 中授予权限，你可以随时在“设置 > 隐私和安全性 > 麦克风”中撤销。

**本地网络。** 为了发现音箱并向其播放，应用在你的家庭网络中使用 mDNS（Bonjour）发现和 AirPlay 2，并会 ping 音箱和路由器，以便提示网络问题。这些都不会离开你的网络。

**只保存在你电脑上的内容。**

- 设置（包括上次使用的音箱的名称和网络地址、你为各个应用做的选择）保存在你用户目录下的设置文件中（`%APPDATA%\HomePodCast`；Microsoft Store 版可能改为保存在应用自己的存储中，卸载应用时由 Windows 删除）。
- 同一位置还有一个日志文件，记录应用做了什么，例如音箱名称及其网络地址、音频设备名称和被路由的应用名称。它的用途是让你在反馈问题时可以附上；除非你自己发送，它不会离开你的电脑。
- 如果你填写了 VLC 网页界面的密码（用于让 VLC 跟随音箱延迟同步），它会用 Windows 数据保护（DPAPI）加密保存，只有你的 Windows 账户能解密。
- 为了让视频播放器保持同步，应用会读取 mpv 和 VLC 的设置文件，并与你电脑上的 VLC 网页界面通信；为了按应用分配声音，它会查看哪些应用正在播放声音。这些都只在你的电脑上进行。

**互联网。** 只有当你打开更新检查或点击 **检查更新** 时，HomePodCast 才会连接互联网：它向 GitHub 的服务器查询 HomePodCast 的最新版本；如果你选择更新，新版本从 GitHub 下载。与任何网络请求一样，GitHub 会看到你的 IP 地址（见 [GitHub 隐私声明](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)）。Microsoft Store 版由 Store 负责更新。应用中的链接（例如本项目主页）会在你的浏览器中打开。

**浏览器扩展。** 扩展只与你本机上的 HomePodCast 应用通信（`http://127.0.0.1:47100`），获取当前的音频延迟，用来让你正在观看的网页视频同步延迟。它不向其他任何地方发送数据，也不收集任何数据。

**Microsoft Store。** 如果你从 Microsoft Store 获取 HomePodCast，下载、安装和更新由 Microsoft 依照 [Microsoft 隐私声明](https://privacy.microsoft.com/privacystatement) 处理。Microsoft 会通过合作伙伴中心向开发者提供汇总统计，例如安装数量和崩溃报告；这些信息不会让开发者识别出你本人。

**儿童。** HomePodCast 不收集任何人的数据，包括儿童。

**变更。** 本政策的变更会发布在此文件中，修改历史见[代码仓库](https://github.com/qiyinxi/HomePodCast/commits/main/PRIVACY.md)。

**联系方式。** 如有问题或疑虑，请在 <https://github.com/qiyinxi/HomePodCast/issues> 提交 issue。
