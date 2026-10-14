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
- **Don't switch the branch of a checkout you didn't create.** Other agents may be committing in the main checkout at
  the same moment (2026-10-12: a new branch checked out there took another agent's next commits with it). Start your
  own work in your own `git worktree`, and check `git branch --show-current` before you commit.

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
  the extension), `gui-smoke` (every page through UI Automation, screenshots in the `gui-screenshots` artifact) and
  `airplay-compat-e2e` (third-party AirPlay: a shairport-sync receiver on Linux). The `release` job needs all of them; main requires
  them.
- What only a person can check is in `docs/device-test-checklist.zh-CN.md`: a short per-release regression, one-time
  verifications (each says which change makes it worth re-testing), experimental features, and a list of what is
  already automated. When you automate an item, move it to that list with its CI job or test class.

## Conventions

- **UI text** is written in Simplified Chinese in the code (`L.T("…")`, `L.F("…{0}", …)`) and translated in all 14
  tables in `src/i18n/*.json`. Add, rename or remove the entry in every table: `LocalizationTests` fails on a missing
  or unused one. No brackets that can wrap alone at a line start in CJK text.
- **Bilingual documents** (README, release notes, RELEASING.md): English first, then 简体中文 with the same content.
- **Comments** follow the surrounding code: say why, and date field observations ("2026-10-12: …") that a rule rests on.
- **Commits**: a short subject line (under 60 characters, what changed, e.g. "Network hint: count possible dropouts"),
  a blank line, then the details: GitHub names each CI run after the subject, and a whole paragraph there gets cut
  off. No Co-Authored-By or other attribution lines.
- **Branches**: `dev` is daily work. Other work goes on its own branch with a PR to `dev` (PR builds run CI). `main`
  changes only through the release PR made by `tools/release.ps1`; never push to it. Tags only through
  `tools/release.ps1`; a pushed tag is never moved or deleted. Details: `docs/RELEASING.md`.
- **Releases**: don't ask before publishing a beta. Publish one whenever dev's CI is green and there is something
  worth trying, and tell the maintainer afterwards. A stable 1.0.0 or later needs the maintainer's OK.
- **Experimental features**: a feature first released in 1.Y.z is labelled experimental (UI, README, release notes)
  until 1.(Y+1).0 drops the label, unless a known problem keeps it. Put `// experimental since 1.Y` next to each label
  in the code so the bump can find them (`git grep "experimental since 1.1"`). Labels that say a feature was never
  tested on real hardware (stereo pairs, multi-room, Apple TV pairing) stay until it has been.

## Working alongside the other agent

Claude and Codex both follow this file (CLAUDE.md only imports it), often on the same day, and they can't talk to each
other: what they share is git, GitHub and the maintainer. 2026-10-14: #22 (Codex) and #30 (Claude) built the same
feature, and #30 conflicted with #27, which was merged into dev while #30 was in review. These rules keep that rare.

- **Your prefix, your branches.** Claude's branches start with `claude/`, Codex's with `codex/`. Commit and push only
  to your own, never to the other agent's branch, not even a merge or a one-line fix. A problem in the other agent's
  open PR goes in a review comment on that PR, or to the maintainer.
- **Your worktrees.** Claude works in `.claude/worktrees/<name>`, Codex in `.worktrees/<name>` or its own worktree
  folder. Don't edit, build, commit in or remove a worktree the other agent made. Remove your own
  (`git worktree remove`) once its PR is merged or closed. The main checkout stays on `dev` (hard rules above).
- **Look before you start.** Run `git fetch` and `gh pr list --state open` first. If an open PR or a fresh branch of the
  other agent covers the same feature or the same files, don't start a parallel one: tell the maintainer, and build
  on it only if they say so.
- **Claim with a draft PR.** Push the branch and open a draft PR to `dev` with your first commit. Its description
  gives the scope and the files it will change, so the other agent can see the area is taken. Mark it ready for review
  when CI is green.
- **Superseding** another agent's PR needs the maintainer's OK. The new PR then says `Supersedes #N` and what it keeps
  and changes, and the old PR gets a comment linking to it. Closing the old PR is left to the maintainer.
- **Stay close to dev.** Keep PRs small and merge them within a day or two. When `dev` moves, merge `origin/dev` into
  your branch: never rebase a pushed branch, never force-push. Resolve each conflict so that both sides' intent
  survives. If that needs a product decision, ask the maintainer. These files conflict most:
  - `.github/workflows/build.yml`: every new job also goes into the `release` job's `needs`.
  - `src/i18n/*.json`
  - `src/Program.cs` (its commands)
  - `src/UI/Pages/SettingsPage.cs`
  - the READMEs, `docs/device-test-checklist.zh-CN.md` and this file
- **Merging.** The author merges its own PR into `dev` once CI is green on a branch that is up to date with dev. Merge
  the other agent's PR only when the maintainer asks.
- **One release at a time.** Before `tools/release.ps1`, check that no release PR is open (`gh pr list --base main`)
  and no tag build is running (`gh run list --limit 5`). The other agent may be in the middle of one: wait for it.
- **Plans for the other agent** go in the main checkout's `docs/plans/` (private: excluded through
  `.git/info/exclude`, never committed or pushed). A plan names the branches, worktrees and PR order. Progress goes in
  the PRs, not in the plan.
- **Real speakers, one agent at a time.** Only the agent the maintainer asked, in its own session, talks to the HomePod
  or any other speaker, even with `stream --silent`. Subagents never do; their tests stay with the in-process fakes.

## Where things are

- `src/Protocol`, `src/Net`: AirPlay 2 (pairing, RTSP, timing, RTP), discovery, takeover rules, network health.
- `src/Audio`: capture (whole output or per app), FIFO, effects. `src/Update`: update check and installer.
- `src/UI`: windows, pages, tray, controls. `src/i18n`: translations.
- `extension/`: the browser extension and its tests (`extension/test/e2e`: the Chrome test).
- `installer/` (WiX MSI), `packaging/msix/` (Store package), `tools/` (release scripts, CI scripts, measuring tools).
- `docs/`: RELEASING.md, the device checklist, feature notes.
