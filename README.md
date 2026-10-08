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

Test device: HomePod (2nd generation), HomePod software 27.0 (24J361).

## Features

- AirPlay 2 realtime audio: transient pairing, encrypted throughout, latency adjustable from 100 to 500 ms in 1 ms steps
- Tray app: finds the speaker on its own, reconnects automatically, resumes after sleep, and backs off instead of fighting when another device takes the speaker over
- Mixer: volume, mute and a level meter for every app
- A/V sync test: a screen flash plus a click show at a glance how far the sound lags the picture
- Browser extension (in development): delays the picture on YouTube and bilibili so it lines up with the HomePod's sound
- Interface in English, Simplified Chinese, Traditional Chinese and Japanese

## Usage

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Run `HomePodCast.exe`. The first time, it asks to add a firewall rule (local network only, active on private networks only): the HomePod has to connect back to the PC for clock sync and to request lost packets again.
3. The app finds the HomePod and connects by itself. Muting the PC's default output device is fine: audio is captured before the system volume is applied, so the HomePod keeps playing.

The interface follows the Windows display language (English unless Windows is set to Chinese or Japanese). To pick another language, right-click the tray icon and choose Language, or set `"Language"` in `%APPDATA%\HomePodCast\config.json` to `"en"`, `"zh-CN"`, `"zh-TW"`, `"ja"` or `"auto"`.

Command line (for troubleshooting):

```
HomePodCast.exe scan                         list the AirPlay speakers on the local network
HomePodCast.exe stream --host <IP> --latency 120 --seconds 30
HomePodCast.exe mutetest                     check that capture still works while muted
```

## Building

Requires the .NET 10 SDK.

```
dotnet build src -c Release          # output goes to app\
dotnet test tests\HomePodCast.Tests  # protocol, audio and translation unit tests
```

`tools\measure_av.py` computes the audio/video offset from a phone video; `tests\vectors\gen_vectors.py` generates reference data for the pairing algorithm with srptools, the library pyatv uses.

Interface texts are written in Simplified Chinese inside `L.T("…")` / `L.F("…{0}", x)`; their translations live in `src/i18n/{en,zh-TW,ja}.json`, and a unit test fails when one is missing. The browser extension's texts are in `extension/_locales`.

## Branches and releases

| Branch / tag | Purpose | Rules |
| --- | --- | --- |
| `dev` | Day-to-day development | Every push runs CI and produces a preview package |
| `main` | Code verified on a real HomePod | Accepts PRs from `dev` only; CI must pass |
| `vX.Y.Z` tags | Releases | Tagged on `main`; built and published as a GitHub Release automatically |
| `release/x.y` | Patches for older versions | Branched from the matching tag only when needed |

CI can't reach a HomePod, so an A/V sync test on real hardware is done before `dev` is merged into `main`.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

- Only binaries built by GitHub Actions from the source code in this public repository are signed.
- Committers and reviewers: [@qiyinxi](https://github.com/qiyinxi)
- Approvers: [@qiyinxi](https://github.com/qiyinxi)

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
