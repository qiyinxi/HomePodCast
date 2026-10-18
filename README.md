# HomePodCast

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md)

Streams your Windows PC's sound to a HomePod over AirPlay 2 in real time: a low-latency setup made for gaming and watching videos.

TuneBlade no longer makes a sound on HomePod software 27. HomePodCast is a native C# implementation written from scratch: a single exe of about 350 KB, no virtual sound card needed.

## Supported outputs — experimental

Third-party receivers are intended mainly for music. AirPlay 1 compatibility and third-party AirPlay 2 are experimental; third-party AirPlay 2 has **not been tested on real hardware**. HomePod's existing transient pairing, NTP, realtime PCM and scene settings are unchanged.

| Output type | Connection | Verification |
| --- | --- | --- |
| HomePod | Existing AirPlay 2 realtime | One HomePod 2 on real hardware |
| Apple TV HD / 4K | AirPlay 2 realtime after PIN pairing | Simulated receiver; hardware untested |
| Older Apple AirPlay receivers | AirPlay 1 compatibility | shairport-sync in CI; hardware untested |
| Third-party AirPlay 2 | Realtime, then advertised AirPlay 1, then buffered; remembers the last working mode | shairport-sync AirPlay 2 + nqptp in CI; hardware untested |
| Third-party AirPlay 1 | AirPlay 1 compatibility | shairport-sync classic in CI |
| Unknown receivers | Realtime → buffered → AirPlay 1, with each refusal logged | Protocol fakes and software receivers only |
| Windows local output | WASAPI render endpoint selected by exact ID | Fake endpoint/clock tests; mixed hardware groups untested |
| Bluetooth speaker or headphones | Windows endpoint, explicitly marked as Bluetooth by the user | 200 ms starting estimate; calibration and hardware testing required |
| DLNA / UPnP MediaRenderer | SSDP discovery, HTTP audio and SOAP control | Fake renderers and gmrender-resurrect filesink in CI; hardware untested |

| Mode | Recommended minimum | Default | Choices / range |
| --- | --- | --- | --- |
| HomePod realtime | 105 ms with its usual 85 ms processing time | 120 ms; Gaming 105 ms | Existing processing time + 15 … 500 ms |
| Apple TV realtime | Receiver processing time + 30 ms | 200 ms | Processing time + 15 … 500 ms |
| Third-party realtime | max(150 ms, processing time + 50 ms) | 250 ms | max(120 ms, processing time + 30 ms) … 500 ms |
| Third-party buffered | 1.0 s | 2.0 s | 1.0 / 1.5 / 2.0 s |
| AirPlay 1 compatibility | 1.0 s; AirPort Express 2.0 s | 2.0 s | 1.0 / 1.5 / 2.0 s |

Without a reported third-party processing time, the estimate is 150 ms and the offered floor is 200 ms. Values below the recommendation show a dropout warning. Receivers may ignore the requested latency; these defaults are software-tested starting points.

Speaker info offers a per-device connection override. Compatibility modes replace HomePod scene buttons with their own latency selector; their settings do not overwrite HomePod scenes. The active protocol and reported processing time appear in Speaker info. On first compatibility connection, a one-time notice explains the roughly two-second delay.

Compatibility mode is suited to music, with too much delay for gaming. The browser extension's picture-delay limit remains **2000 ms**; a greater sound delay cannot be fully matched. The A/V sync test can still calibrate the actual offset. Third-party AirPlay 2 realtime/buffered and AirPlay 1 receivers can join live multi-speaker groups (experimental, not hardware-tested). One capture timeline and group delay coordinate the outputs; receiver behavior and calibration determine actual alignment. Local-file groups still require HomePods. See [group transport details](docs/multi-output-receivers.md).

FairPlay-protected and MFi-authenticated-only receivers are unsupported. In particular, an XGIMI Z6X was checked by handshake only: its audio session requires FairPlay, so it is **unsupported**, not a verified playback device. Do not infer compatibility from an emulated Apple TV model name. For other receivers, reports should include `HomePodCast.exe scan --txt`, the selected mode, the actual connection delay and the refused request from the log.

## Live output groups — experimental since 1.2

On **Multi-speaker**, choose **Live computer audio**, an output type, then **Refresh outputs** and **Add output**.
Pair Bluetooth in Windows first and explicitly mark its exact endpoint as **Bluetooth output**; names do not identify
the transport. Windows does not report Bluetooth link delay, so 200 ms is only a starting estimate. An output cannot
be the same Windows endpoint as the capture source. Opening the editor starts no discovery or audio.

Each output has stereo/left/right channels, volume offset, required/optional behavior and ±5000 ms trim. Automatic
group delay covers the slowest output; manual delay still respects device floors. Save a named group, select it under
**Saved groups** on Home, then connect. Saving an offline preference or selecting a group while idle never connects it.
Optional Apple AirPlay, RAOP, local and DLNA outputs recover independently without reclaiming another sender's stream.

**Manual calibration** opens silently, without a microphone. Its optional explicit test tone is capped at 25%. Film
separately near the reference and target speakers, subtract their sound-to-flash frame offsets, then apply the result
to the editor draft; **Apply** on the group page makes it effective. AirPlay 2 targets tight common-timeline sync;
AirPlay 1/Bluetooth provide basic alignment after calibration; DLNA is rough alignment for different rooms. Mixed
hardware groups remain unverified. Local files still require HomePods, with independent positions and 0–500 ms delays.
See [output groups and calibration](docs/multi-output-receivers.md) and [DLNA limits](docs/dlna.md).

## Measurements

| Scenario | Value |
| --- | --- |
| Sound behind the picture (latency set to 120 ms, measured from a 60 fps phone video) | 156 ms |
| Sound behind the picture (latency set to 105 ms, estimated) | about 141 ms |
| Streaming stats (dropouts / late sends / retransmits) | 0 / 0 / 0 |
| Memory (private) | about 28 MB |

## Tested hardware

HomePodCast is a one-person project and has been tested on exactly one setup:

- Speaker: one HomePod (2nd generation), HomePod software 27.0 (24J361), on Wi-Fi
- PC: one Windows 11 PC whose default output is a virtual sound card

Not tested on real hardware:

- HomePod mini, HomePod (1st generation), Apple TV, and AirPlay speakers from other brands
- **Stereo pairs and multi-room**: built from protocol research and tested only against simulated speakers
- **Wireless cinema** (one HomePod per channel): tested only against simulated speakers and generated files
- Physical sound cards and headsets, other Windows versions, and how a wired vs Wi-Fi PC behaves

If you try it on other hardware, please open an issue with the device model, its software version, the lowest latency setting that plays cleanly, and a diagnostic report (Diagnostics → Diagnostic report → **Export diagnostics**: a ZIP with the redacted logs and the measurements; you can check its contents before attaching it). The raw log, `homepodcast.log`, is for your own troubleshooting: it names your speakers and holds IP addresses, so don't attach it to a public issue. Diagnostics → Log → **Open log folder** shows it; it is in `%APPDATA%\HomePodCast`, or for the Microsoft Store version in `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`.

## Features

- AirPlay 2 realtime audio: transient pairing, encrypted throughout, latency adjustable from 100 to 500 ms in 1 ms steps
- One window with Home, Mixer, Microphone, Equalizer and dedicated tool pages; follows Windows light/dark mode
- Scenes: Recommended (120 ms, the default), Gaming (105 ms), Music (500 ms, the most headroom), Movies (200 ms, works with the browser extension and your video player's audio-delay setting), or your own value
- Tray app: finds the speaker on its own, reconnects automatically, resumes after sleep, and backs off instead of fighting when another device takes the speaker over
- Never cuts off another device unasked: while an iPhone or a Mac plays to the HomePod, Connect asks first, and an automatic connect waits until the speaker is free (so does a playback profile that switches to it)
- Disconnect at night (experimental): for a PC left on with the screens off, the HomePod is let go during the hours you set once the keyboard, mouse and sound have been idle for a while (5 to 120 minutes), so other devices can use it; when the hours end it connects again like the auto-connect at startup, never cutting off a device that is playing
- Apple TV home theater: a HomePod set up as an Apple TV's speaker (the Apple TV keeps it busy even when idle) is taken over for as long as HomePodCast is connected, with no PIN pairing; the Apple TV reconnects to it the next time it plays. Settings can make Connect ask first instead (experimental, not yet tested with a real Apple TV)
- Right-click the tray icon for a quick panel: volume and mute, scene, night mode, microphone, connect or disconnect
- Mixer: volume, mute and a level meter for every app, plus optional per-app routing (HomePod / this PC / both) or routing by sound card (choose which output device is captured), see below
- Volume limit, night mode (dynamic-range compression plus reduced bass), global hotkeys, and keyboard volume keys for the HomePod (it follows the Windows volume, or the keys control only the HomePod while streaming); the PC can be muted by itself while streaming so only the HomePod plays (experimental), see below
- Microphone monitoring with reverb and mic EQ: choose HomePod, this PC or both, with a destination explanation; off at each start. A separate Equalizer page controls all sound streamed to the speaker
- Stereo pair and multi-room sync (experimental, not yet tested on real speakers)
- Wireless cinema (experimental, not yet tested on real speakers): the audio track of a local 5.1 or 7.1 file with each channel on a HomePod of its own, up to seven plus one for the LFE channel; channels without a speaker fold into the neighboring ones, and speaker delays can be measured with a microphone (at most 25 % volume). See [Wireless cinema](docs/WIRELESS_CINEMA.md#english)
- Apple TV after a one-time PIN pairing: click Connect and type the 4 digits the TV shows; with HomePods as its home theater speakers, they play the sound (experimental, not yet tested on a real Apple TV)
- Diagnostics → A/V sync test: a screen flash plus a click show at a glance how far the sound lags the picture
- Speaker info: model, system and AirPlay version, IP address, Wi-Fi MAC address (find the HomePod in your router's client list by it to see its band or give it a fixed IP), Bluetooth address and device ID
- Browser extension (beta): delays the picture on YouTube and bilibili so it lines up with the HomePod's sound. The Music scene's 500 ms is meant for listening; video plays smoothest with Movies or Recommended (the extension's popup suggests this when the picture delay is over 300 ms)
- Synchronized viewing window (experimental): for players without an audio-delay setting, a separate module shows a window you pick (or a whole screen) delayed by the HomePod's sound lag, in full screen if you like, with fine adjustment; the picture stays on the graphics card all the way. It is the optional `HomePodCast-video-window-<version>.zip`, not in the Microsoft Store version. See [Synchronized viewing window](docs/VIDEO_WINDOW.md#english)
- Update check (off by default, see [Updates](#updates)); with the MSI, a new version installs in one click after its SHA-256 is verified
- Interface in 15 languages, including English, Simplified/Traditional Chinese, Japanese and the supported European languages

Playback profiles, per-speaker/source A/V calibration, independent per-app HomePod volume, redacted diagnostic reports, and synchronized browser fullscreen with ±5/±20 ms controls are also available. See [Playback profiles, calibration and diagnostics](docs/PLAYBACK_FEATURES.md#english) for their controls and limitations. The browser extension adds each site's own offset on top of the calibration: if you had already compensated per site, reset those site offsets after calibrating.

## Usage

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
2. Download HomePodCast from [Releases](https://github.com/qiyinxi/HomePodCast/releases). There are two packages, plus an optional module:
   - **Installer, recommended: `HomePodCast-<version>-x64.msi`.** You choose the install folder (default `C:\Program Files\HomePodCast`). Setup adds a Start menu shortcut and the firewall rule the app needs. To upgrade, run the newer MSI. To uninstall, use Settings → Apps; if you turned on "Start with Windows", turn it off first.
   - **Portable: `HomePodCast-<version>-win-x64.zip`.** Unzip it anywhere and run `HomePodCast.exe`. The first time, it asks to add the firewall rule.
   - **Optional: `HomePodCast-video-window-<version>.zip`**, the synchronized viewing window (see Features). Unzip it anywhere and choose its folder under **A/V sync → Synchronized viewing window (experimental)**. Updates of the app don't update it: download the module of the same version again.

   The firewall rule allows the local network only and is active on private networks only. The HomePod needs it to connect back to the PC for clock sync and to request lost packets again. If Windows has put your network in the Public category (this can happen after switching from Wi-Fi to Ethernet), the rule doesn't apply: HomePodCast tells you and can open Windows Settings, where you set the network profile type to Private (only for a home network you trust). Both packages keep your settings and the log in `%APPDATA%\HomePodCast`. The Microsoft Store version keeps what it writes in its own storage, `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`, which Windows removes when you uninstall it; Diagnostics → Log → **Open log folder** opens the right folder in either case.
3. The app finds the HomePod and connects by itself. Audio is captured before the Windows volume is applied, so the Windows volume never changes the sound that is sent; the HomePod has its own volume. By default the Windows volume is the HomePod volume: the two stay in step (on connecting, both go to the lower of the two), and muting Windows mutes the HomePod too. If your default output is the PC speakers and you want to hear only the HomePod, turn on Settings → "Mute the PC while streaming", or click "Play on the HomePod only" when Home says the PC is playing too (see [Keyboard volume keys](#keyboard-volume-keys)).

The interface follows the Windows display language (English when HomePodCast has no translation for it). To pick another language, use Settings → Language, or set `"Language"` in `%APPDATA%\HomePodCast\config.json` to a language code such as `"en"`, `"de"` or `"zh-TW"` (the names of the files in `src/i18n`, plus `"zh-CN"`), or to `"auto"`.

Command line (for troubleshooting):

```
HomePodCast.exe scan                         list the AirPlay speakers on the local network
HomePodCast.exe stream --host <IP> --latency 120 --seconds 30
HomePodCast.exe mutetest                     check that capture still works while muted
```

## Stream everything vs. per-app routing

By default HomePodCast captures the PC's whole sound output and sends all of it to the HomePod. That path adds no latency of its own, so it is the one to use for gaming. Whether the PC's own speakers also play is up to the Windows volume. To hear everything on the HomePod only, turn on Settings → "Mute the PC while streaming" (see [Keyboard volume keys](#keyboard-volume-keys)); muting Windows by hand with the default "HomePod follows the Windows volume" would mute the HomePod too.

Per-app routing sends each app to the HomePod, this PC, or both (set on the Mixer page), for example game audio to the HomePod and voice chat on your headset. It costs latency:

| | Stream everything (default) | Per-app routing |
| --- | --- | --- |
| When it's on | Whenever routing isn't | As soon as any app has its own destination, or the default is "This PC" |
| How sound is captured | The whole output in one piece | Each app separately (Windows process loopback) |
| Extra latency to the HomePod | None | About 35 ms (measured from playback to send: 21 ms vs 55 ms) |
| Web video lip-sync (browser extension) | Compensated | Compensated, extra 35 ms included |
| Games | Lowest latency | 35 ms later, can't be compensated |
| Apps set to "HomePod" | — | Silent on this PC; shown at 0% in the Windows volume mixer |
| System sounds | Follow the Windows volume | Play on this PC only |
| Requires | Windows 10/11 | Windows 10 version 2004 or later |

The app asks for confirmation the first time routing turns on. Set every app back to "Default" with HomePod as the default to return to streaming everything. If HomePodCast is killed while routing, apps set to "HomePod" stay at 0% until it runs again.

### Routing by sound card (capture device)

Mixer → Capture device decides which output device's sound is sent; by default it follows the Windows default output. Choose a device you don't listen to, then set the apps you want on the HomePod (a game, say) to play on it, in the Windows app volume settings (the Mixer page has a button for them) or in the app's own settings; voice chat and everything else keep playing on your headphones. The whole device is captured in one piece, so like streaming everything it adds no latency, unlike the roughly 35 ms of per-app routing.

- It needs a spare or virtual sound card, such as VB-CABLE, NetEase UU's virtual sound card, Steam Streaming Speakers, or an HDMI monitor without speakers. HomePodCast can't install a virtual sound card itself: that is a kernel driver that must be signed by Microsoft.
- Some games only pick their output device at launch; restart the game after changing it.
- If the chosen device is unplugged, disabled or uninstalled, the default output is not used instead (that could send your voice chat to the speaker): capture pauses, the Home page says so, and it resumes when the device is back.
- Per-app routing still works on top; its rules apply to the apps on that device.
- With a capture device chosen, "HomePod follows the Windows volume" follows that device's volume, while the volume keys only change the Windows default output. To control the HomePod with the volume keys, make that device the default output.
- While streaming, the mic's "This PC" output pauses if it uses the device being captured (for example both follow the default output); otherwise your voice would reach the HomePod twice.

## Keyboard volume keys

Settings → Keyboard volume keys decides what volume up, volume down and mute do to the HomePod:

- **HomePod follows the Windows volume** (default): while streaming, the Windows volume is the HomePod volume. Windows 0–100% maps to 0 up to the volume limit; the keys and the Windows volume slider move the HomePod, and changing the HomePod in this app moves Windows. On connecting, and when the limit changes, both sides go to the lower of the two, so neither gets louder by itself. Muting Windows mutes the HomePod.
- **Control the HomePod while streaming**: while streaming, the three keys change only the HomePod (2% per step) and leave the Windows volume alone; a small indicator at the bottom of the screen shows the HomePod volume (not over exclusive full-screen games). When not streaming, the keys change Windows as usual.
- **Only while Windows is muted**: the earlier behaviour. Key presses go to the HomePod only while Windows is muted or at 0%, and Windows stays muted.
- **Off**: the keys change only Windows.

**Mute the PC while streaming** (experimental, Settings, under the keyboard volume keys; off by default): for when the PC speakers (or headphones) are the default output and you want to hear only the HomePod. While streaming, HomePodCast mutes the Windows default output; what is sent to the HomePod is captured before the Windows volume, so it keeps playing. The PC's sound comes back when the session ends (you disconnect, another device takes the speaker over, the night disconnect) and when HomePodCast quits; a short dropout or a reconnect keeps it muted. HomePodCast only unmutes what it muted itself: if Windows was muted already, it stays muted, and if you unmute Windows yourself while streaming, that is left alone until the next connection. With this on, "HomePod follows the Windows volume" works like "Control the HomePod while streaming", since following a muted Windows would mute the HomePod. Nothing is muted while a capture device other than the default output is chosen (see [Routing by sound card](#routing-by-sound-card-capture-device)): what plays on the default output then is meant to stay there. If HomePodCast is killed while streaming, the PC stays muted until you unmute it; the log says what was muted.

While streaming with this off, Home shows "The PC is playing too" when the sound also plays on the PC's speakers or headphones (not on HDMI audio or a virtual sound card, not while Windows is muted or at 0%, and not with per-app routing, where you choose per app where it plays). **Play on the HomePod only** turns the setting on and mutes the PC at once; with "HomePod follows the Windows volume" or "Off" it also sets the keys to "Control the HomePod while streaming". **Don't show again** hides the hint for good.

If the PC has no usable output device (no sound driver, or the device is disabled or unplugged), "HomePod follows the Windows volume" and "Only while Windows is muted" also take the keys over while streaming, until a device is back. The keys still change Windows while a program running as administrator is in front, and with remotes or mice whose software sends media commands instead of key presses; "HomePod follows the Windows volume" picks those changes up.

## Updates

HomePodCast can look for a newer version among this repository's GitHub releases: About → Updates → **Check for updates**. This is the only request it ever makes outside your local network, and only when you ask for it: automatic checking is **off** by default. Turn on "Check for updates automatically" to check about 30 seconds after startup and every 24 hours (development builds never check by themselves). The request is one HTTPS GET of `api.github.com/repos/qiyinxi/HomePodCast/releases` with `HomePodCast/<version>` as its User-Agent: no account, and nothing about your PC or speaker is sent. Each request is written to the log.

- **Update channel**: Stable offers stable releases only; Beta offers betas too, whichever is newest. A beta that is newer than the latest stable release stays until the next stable release (never a downgrade).
- When an automatic check finds a new version, a tray notification appears once and Home shows a banner with **Update**, **Release notes** and **Skip this version**. A manual check answers in Settings.
- **Nothing is installed without a click.** With the MSI, **Update** asks first, then downloads the new MSI and checks it against the SHA-256 that GitHub lists for the file (if it doesn't match, or GitHub lists none, the file is deleted and nothing is installed). HomePodCast then exits (streaming stops for about a minute) and runs the installer, for which Windows asks for administrator permission. Afterwards HomePodCast starts again and tells you the version it was updated to; if you decline the prompt, the old version starts again.
- The portable zip never updates itself: **Update** opens the release page.
- The Microsoft Store version is updated by the Store and never checks GitHub itself.

## FAQ

### The sound stutters now and then

**Why it happens**: the sound crosses two wireless hops, PC → router → HomePod. The HomePod (2nd generation) only has Wi-Fi 4 (802.11n), and people walking by, a raised arm, walls and cabinet doors all block the signal, so once in a while a packet arrives late or gets lost. At 120 ms (Recommended) the HomePod itself takes about 85 ms, which leaves only about 35 ms for network jitter and resent packets; one hiccup longer than that is one stutter.

**What helps, in this order**:

1. Connect the PC with a network cable if you can: two wireless hops become one.
2. Windows' power saving for the wireless adapter: Control Panel → Power Options → Change plan settings → Change advanced power settings → Wireless Adapter Settings → Power Saving Mode: Maximum Performance, both on battery and plugged in.
3. Device Manager → Network adapters → your wireless adapter → Properties → Advanced: set Roaming Aggressiveness to the lowest value; if they are there, also set MIMO Power Save Mode to No SMPS and turn Packet Coalescing off. The names vary by adapter.
4. The HomePod's band: the HomePod (2nd generation) can use 2.4 GHz or 5 GHz, and when the router gives both bands one name (Smart Connect, band steering) it may end up on 2.4 GHz, which is crowded wherever many networks are nearby. Give the 5 GHz band a name of its own (turn Smart Connect off), connect the iPhone to it, and move the HomePod to that network in the Home app. Measured 2026-10-10 in a dense building with thin walls, the HomePod 50 cm from the router: on 2.4 GHz a ping median of 21 ms, p99 about 110 ms and 4% lost, with stutters even at 500 ms; on 5 GHz 2 ms, at most 12 ms, nothing lost. This helps more than placement.
5. Where the HomePod stands: in line of sight of the router, not inside a cabinet.
6. Raise the latency by 10–15 ms: choose Custom under Scene, or press Apply when the Home page suggests it. Below Stream status, the Home page shows how many network hiccups the last 10 minutes had; when they really eat into the margin, a hint says which Wi-Fi hop is to blame (with the PC on a cable, always the HomePod's) and which latency to try. The app never changes the latency by itself. The Dropouts tile counts this connection's gaps: the ones measured on the PC plus the hiccups that ate the margin, which the HomePod likely played as gaps. The HomePod never reports late packets, so that part is an estimate and the number gets a "≈"; hover over the tile to see both parts.

**Reading the log** (`%APPDATA%\HomePodCast\homepodcast.log`, for the Microsoft Store version `%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast\homepodcast.log`; Diagnostics → Log → **Open log folder** shows it): while streaming, the HomePod and the router are pinged every 100 ms, and a round trip over 30 ms or a missing answer is logged as `network: ping to the speaker took 48 ms` (`the router` for the router, `lost` for no answer). The `stats:` line every minute has `rtx=resent/requested`, `rtxMiss=` (packets asked for too late to resend) and `ping=median/p99/max lost=lost/sent router=…`.

- Only the speaker's ping spikes while the router's stays normal: it's the router → HomePod hop; see steps 4 and 5.
- Both spike at the same time: it's the PC's own Wi-Fi; see steps 1–3. With the PC on a cable it's still the HomePod's hop (the router only answered its own pings late), and the hint says so.
- Only the router is slow: routers often answer pings to themselves last; the sound is not affected.

## Building

Requires the .NET 10 SDK.

```
dotnet build src -c Release          # output goes to app\
dotnet test tests\HomePodCast.Tests  # protocol, audio and translation unit tests
```

The MSI installer is built with WiX Toolset v5, a local dotnet tool, by `installer\build.ps1`; see [installer/README.md](installer/README.md).

`tools\measure_av.py` computes the audio/video offset from a phone video; `tests\vectors\gen_vectors.py` generates reference data for the pairing algorithm with srptools, the library pyatv uses.

Interface texts are written in Simplified Chinese inside `L.T("…")` / `L.F("…{0}", x)`; their translations live in `src/i18n/<language>.json` (every file there is a UI language; `en.json` is the reference), and a unit test fails when one is missing or a table does not match `en.json`. The browser extension's texts are in `extension/_locales`.

## Branches and releases

| Branch / tag | Purpose | Rules |
| --- | --- | --- |
| `dev` | Day-to-day development | Every push runs CI and produces a preview package |
| `main` | Code verified on a real HomePod | Accepts PRs from `dev` or a `release/X.Y.Z` branch only; CI must pass |
| `vX.Y.Z` tags | Releases | Tagged on `main`; built and published as a GitHub Release automatically |
| `release/x.y` | Patches for older versions | Branched from the matching tag only when needed |

CI can't reach a HomePod, so an A/V sync test on real hardware is done before `dev` is merged into `main`.

How a release is made (betas, promoting a tested beta to a stable release, what CI checks before publishing): [docs/RELEASING.md](docs/RELEASING.md).

## Code signing

Releases are not code-signed yet, so Windows SmartScreen may warn the first time you run the installer or the exe ("More info" → "Run anyway"). Every release is built by GitHub Actions from the public source code in this repository; download it only from the [Releases](https://github.com/qiyinxi/HomePodCast/releases) page.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it. It talks only to the AirPlay speakers
on the local network that the user selects, and its status endpoint listens on 127.0.0.1 only. The only
request outside the local network is the update check (see [Updates](#updates)), made only when the user
clicks "Check for updates" or turns on automatic checks.

## Acknowledgements

- [pyatv](https://github.com/postlund/pyatv) (MIT): reference for the protocol flow; its dependency srptools serves as the reference for the pairing algorithm.
- [NAudio](https://github.com/naudio/NAudio) (MIT): cross-checking the Windows Core Audio interface definitions.
- [AirFlash](https://github.com/Ding-Kyoma/AirFlash) (GPL-3.0): ideas for low-latency packet sending and buffering.
- [TweetNaCl](https://tweetnacl.cr.yp.to/) (public domain): the Ed25519 and X25519 code, ported to C# for the Apple TV pairing.

## License

[GPL-3.0-or-later](LICENSE). You are free to use, modify and redistribute it; if you redistribute a modified version, it must be released under the GPL as well.
