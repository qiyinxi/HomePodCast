# Notes for coding agents

HomePodCast is a Windows AirPlay 2 sender for HomePods (C#/.NET 10, WinForms with its own Fluent controls), with a
browser extension, an MSI and a Microsoft Store package. One maintainer; several agents (Claude, Codex) work on it,
often in parallel worktrees. Read this before changing anything.

## Hard rules

These protect the maintainer's own setup. Breaking one has happened before and cost them their speaker or their evening.

- **Never start the app's GUI on the maintainer's PC** (`HomePodCast.exe` without a command, `gui`, a test build).
  A copy that finds the HomePod connects by itself and takes it away from the maintainer's running app. If a GUI run
  is truly needed, ask first, and use a test profile (`HOMEPODCAST_PROFILE=<name>`: own config folder and
  single-instance name) whose config has `AutoConnect` false and no `DeviceId`. GUI checks belong in CI (`gui-smoke`).
- **Nothing audible.** Never stream captured audio or tones to a real speaker unless the maintainer asked for that
  test; then at most 25 % volume (`--cap`). Protocol experiments use `HomePodCast.exe stream --silent --cap 10`.
  Never set `HOMEPODCAST_LIVE_AUDIO`.
- **Don't take over a speaker another device is using** (an iPhone, the Apple TV's home theater) without the
  maintainer's explicit OK for that test.
- **Don't install or uninstall** MSIs, MSIX packages or certificates on the maintainer's PC, and don't run the CI
  end-to-end scripts there (`tools/ci/*-e2e.ps1`, `packaging/msix/e2e-test.ps1` refuse outside GitHub Actions anyway).
- **Don't change system, security or network settings** (firewall rules, the network profile, the router, the Apple
  TV): say what to change and let the maintainer do it.
- The maintainer's real `%APPDATA%\HomePodCast` (config, log, `pairings.dat`) is theirs: read a copy they give you;
  never write to it.

## Build and test

```
dotnet build src -c Release
dotnet test tests\HomePodCast.Tests
node --test (Get-ChildItem extension/test -Filter *.test.js).FullName
```

- A Debug build goes to `app\HomePodCast.exe`: a firewall rule on the maintainer's PC points at that path, so keep it.
- The C# tests use in-process fakes (AirPlay receiver, HomePod, Apple TV, release feed, local API); they need no
  speaker and make no sound. Every change keeps them green, and new behaviour gets a test.
- CI (`.github/workflows/build.yml`, every push and PR to dev/main/release) also runs, on its own runners:
  `update-e2e` (in-app MSI update), `msi-e2e` (installer), `msix-e2e` (Store package), `extension-e2e` (Chrome with
  the extension) and `gui-smoke` (every page through UI Automation, screenshots in the `gui-screenshots` artifact). The
  `release` job needs all of them; main requires them.
- What only a person can check is in `docs/device-test-checklist.zh-CN.md`: a short per-release regression and
  one-time verifications. Mark items you automate there (「CI 自动测试：…」 or 「单元测试：…」).

## Conventions

- **UI text** is written in Simplified Chinese in the code (`L.T("…")`, `L.F("…{0}", …)`) and translated in all 14
  tables in `src/i18n/*.json`. Add, rename or remove the entry in every table: `LocalizationTests` fails on a missing
  or unused one. No brackets that can wrap alone at a line start in CJK text.
- **Bilingual documents** (README, release notes, RELEASING.md): English first, then 简体中文 with the same content.
- **Comments** follow the surrounding code: say why, and date field observations ("2026-10-12: …") that a rule rests on.
- **Commits**: plain descriptive messages, no Co-Authored-By or other attribution lines.
- **Branches**: `dev` is daily work. Other work goes on its own branch with a PR to `dev` (PR builds run CI). `main`
  changes only through the release PR made by `tools/release.ps1`; never push to it. Tags only through
  `tools/release.ps1`; a pushed tag is never moved or deleted. Details: `docs/RELEASING.md`.
- **Releases**: any beta may be published without asking; a stable 1.0.0 or later needs the maintainer's OK.
- **Experimental features**: a feature first released in 1.Y.z is labelled experimental (UI, README, release notes)
  until 1.(Y+1).0 drops the label, unless a known problem keeps it. Put `// experimental since 1.Y` next to each label
  in the code so the bump can find them (`git grep "experimental since 1.1"`). Labels that say a feature was never
  tested on real hardware (stereo pairs, multi-room, Apple TV pairing) stay until it has been.

## Where things are

- `src/Protocol`, `src/Net`: AirPlay 2 (pairing, RTSP, timing, RTP), discovery, takeover rules, network health.
- `src/Audio`: capture (whole output or per app), FIFO, effects. `src/Update`: update check and installer.
- `src/UI`: windows, pages, tray, controls. `src/i18n`: translations.
- `extension/`: the browser extension and its tests (`extension/test/e2e`: the Chrome test).
- `installer/` (WiX MSI), `packaging/msix/` (Store package), `tools/` (release scripts, CI scripts, measuring tools).
- `docs/`: RELEASING.md, the device checklist, feature notes.
