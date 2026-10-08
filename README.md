# HomePodCast

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md)

Streams your Windows PC's sound to a HomePod over AirPlay 2 in real time: a low-latency setup made for gaming and watching videos.

TuneBlade no longer makes a sound on HomePod software 27. HomePodCast is a native C# implementation written from scratch: a single exe of about 350 KB, no virtual sound card needed.

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
- Physical sound cards and headsets, other Windows versions, and how a wired vs Wi-Fi PC behaves

If you try it on other hardware, please open an issue with the device model, its software version, the lowest latency setting that plays cleanly, and the log (`%APPDATA%\HomePodCast\homepodcast.log`).

## Features

- AirPlay 2 realtime audio: transient pairing, encrypted throughout, latency adjustable from 100 to 500 ms in 1 ms steps
- One window with Home, Mixer, Microphone and effects, and Settings; follows Windows light/dark mode
- Scenes: Recommended (120 ms, the default), Gaming (105 ms), Music (500 ms, the most headroom), Movies (200 ms, works with the browser extension and your video player's audio-delay setting), or your own value
- Tray app: finds the speaker on its own, reconnects automatically, resumes after sleep, and backs off instead of fighting when another device takes the speaker over
- Right-click the tray icon for a quick panel: volume and mute, scene, night mode, microphone, connect or disconnect
- Mixer: volume, mute and a level meter for every app, plus optional per-app routing (HomePod / this PC / both), see below
- Volume limit, night mode (dynamic-range compression plus reduced bass), global hotkeys, and keyboard volume keys for the HomePod (it follows the Windows volume, or the keys control only the HomePod while streaming), see below
- Microphone to the HomePod with reverb and EQ, low-latency monitoring on headphones, and EQ presets for everything sent to the speaker
- Stereo pair and multi-room sync (experimental, not yet tested on real speakers)
- A/V sync test: a screen flash plus a click show at a glance how far the sound lags the picture
- Browser extension (beta): delays the picture on YouTube and bilibili so it lines up with the HomePod's sound
- Interface in English, Simplified Chinese, Traditional Chinese and Japanese

## Usage

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
2. Download HomePodCast from [Releases](https://github.com/qiyinxi/HomePodCast/releases). There are two packages:
   - **Installer, recommended: `HomePodCast-<version>-x64.msi`.** You choose the install folder (default `C:\Program Files\HomePodCast`). Setup adds a Start menu shortcut and the firewall rule the app needs. To upgrade, run the newer MSI. To uninstall, use Settings → Apps; if you turned on "Start with Windows", turn it off first.
   - **Portable: `HomePodCast-<version>-win-x64.zip`.** Unzip it anywhere and run `HomePodCast.exe`. The first time, it asks to add the firewall rule.

   The firewall rule allows the local network only and is active on private networks only. The HomePod needs it to connect back to the PC for clock sync and to request lost packets again. Both packages keep your settings in `%APPDATA%\HomePodCast`.
3. The app finds the HomePod and connects by itself. Audio is captured before the Windows volume is applied, so the Windows volume never changes the sound that is sent; the HomePod has its own volume. By default that volume follows the Windows volume, and muting Windows mutes the HomePod too. If your default output is the PC speakers and you want to hear only the HomePod, set Settings → Keyboard volume keys to "Control the HomePod while streaming" and mute Windows (see [Keyboard volume keys](#keyboard-volume-keys)).

The interface follows the Windows display language (English when HomePodCast has no translation for it). To pick another language, use Settings → Language, or set `"Language"` in `%APPDATA%\HomePodCast\config.json` to a language code such as `"en"`, `"de"` or `"zh-TW"` (the names of the files in `src/i18n`, plus `"zh-CN"`), or to `"auto"`.

Command line (for troubleshooting):

```
HomePodCast.exe scan                         list the AirPlay speakers on the local network
HomePodCast.exe stream --host <IP> --latency 120 --seconds 30
HomePodCast.exe mutetest                     check that capture still works while muted
```

## Stream everything vs. per-app routing

By default HomePodCast captures the PC's whole sound output and sends all of it to the HomePod. That path adds no latency of its own, so it is the one to use for gaming. Whether the PC's own speakers also play is up to the Windows volume. To hear everything on the HomePod only, set Settings → Keyboard volume keys to "Control the HomePod while streaming" (or "Only while Windows is muted") and mute Windows; with the default, "HomePod follows the Windows volume", muting Windows mutes the HomePod too.

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

## Keyboard volume keys

Settings → Keyboard volume keys decides what volume up, volume down and mute do to the HomePod:

- **HomePod follows the Windows volume** (default): the keys change Windows as usual, and while streaming the HomePod follows. Windows 0–100% maps to 0 up to the volume limit, and muting Windows mutes the HomePod.
- **Control the HomePod while streaming**: while streaming, the three keys change only the HomePod (2% per step) and leave the Windows volume alone; a small indicator at the bottom of the screen shows the HomePod volume (not over exclusive full-screen games). When not streaming, the keys change Windows as usual. Use this when the PC speakers are the default output and you want to hear only the HomePod: mute Windows, then use the keys for the HomePod.
- **Only while Windows is muted**: the earlier behaviour. Key presses go to the HomePod only while Windows is muted or at 0%, and Windows stays muted.
- **Off**: the keys change only Windows.

If the PC has no usable output device (no sound driver, or the device is disabled or unplugged), "HomePod follows the Windows volume" and "Only while Windows is muted" also take the keys over while streaming, until a device is back. The keys still change Windows while a program running as administrator is in front, and with remotes or mice whose software sends media commands instead of key presses; "HomePod follows the Windows volume" picks those changes up.

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
| `main` | Code verified on a real HomePod | Accepts PRs from `dev` only; CI must pass |
| `vX.Y.Z` tags | Releases | Tagged on `main`; built and published as a GitHub Release automatically |
| `release/x.y` | Patches for older versions | Branched from the matching tag only when needed |

CI can't reach a HomePod, so an A/V sync test on real hardware is done before `dev` is merged into `main`.

## Code signing

Releases are not code-signed yet, so Windows SmartScreen may warn the first time you run the installer or the exe ("More info" → "Run anyway"). Every release is built by GitHub Actions from the public source code in this repository; download it only from the [Releases](https://github.com/qiyinxi/HomePodCast/releases) page.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it. It talks only to the AirPlay speakers
on the local network that the user selects, and its status endpoint listens on 127.0.0.1 only.

## Acknowledgements

- [pyatv](https://github.com/postlund/pyatv) (MIT): reference for the protocol flow; its dependency srptools serves as the reference for the pairing algorithm.
- [NAudio](https://github.com/naudio/NAudio) (MIT): cross-checking the Windows Core Audio interface definitions.
- [AirFlash](https://github.com/Ding-Kyoma/AirFlash) (GPL-3.0): ideas for low-latency packet sending and buffering.

## License

[GPL-3.0-or-later](LICENSE). You are free to use, modify and redistribute it; if you redistribute a modified version, it must be released under the GPL as well.
