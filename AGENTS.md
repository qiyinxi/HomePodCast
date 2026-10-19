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
- Tests listen on loopback only (`IPAddress.Loopback`, never `Any` or a LAN address), fakes and the code under test
  alike. Anything else makes Windows ask for a firewall rule, once per test build path, so once per worktree; a prompt
  left open at night held up the maintainer's other automation. `LoopbackOnlyTests` fails on such a listener, and on
  a UDP socket that only `Connect` or a send bound: Windows lists it on 0.0.0.0. For the local address that reaches a
  destination, use `Native.SourceAddressFor`, not a connected probe socket.
- CI (`.github/workflows/build.yml`, every push and PR to dev/main/release) also runs, on its own runners:
  `update-e2e` (in-app MSI update), `msi-e2e` (installer), `msix-e2e` (Store package), `extension-e2e` (Chrome with
  the extension), `gui-smoke` (every page through UI Automation, screenshots in the `gui-screenshots` artifact) and
  `airplay-compat-e2e` (third-party AirPlay: a shairport-sync receiver on Linux). The `release` job needs all of them;
  main requires them.
- What only a person can check is in `docs/device-test-checklist.zh-CN.md`: a short per-release regression, one-time
  verifications (each says which change makes it worth re-testing), experimental features, and a list of what is
  already automated. When you automate an item, move it to that list with its CI job or test class.

## Conventions

- **UI text** is written in Simplified Chinese in the code (`L.T("…")`, `L.F("…{0}", …)`) and translated in all 14
  tables in `src/i18n/*.json`. Add, rename or remove the entry in every table: `LocalizationTests` fails on a missing
  one and lists unused ones in its output (it doesn't fail on those), so remove a text's entries with the text. No
  brackets that can wrap alone at a line start in CJK text.
- **Names of settings and controls** use the established professional term, as audio software and Windows itself say
  it, with the English term in parentheses where it helps. Explanations go in the description line or the tooltip,
  never in the name. Invented descriptive phrases are easy to misread. The maintainer's examples, 2026-10-15:
  - 「麦克风监听」, not 「使用麦克风」 or 「把麦克风混进推流」;
  - 衰减（Room Size）, 高频阻尼（Damping）, 干湿比（Wet/Dry）, 预延迟（Pre-delay）, not 「房间大小」 or 「混响比例」.
- **Pages stay small.** Each page in the left navigation holds a limited number of features. A crowded page is worse
  than one more page, so a feature that doesn't belong gets its own page rather than another section. That is why
  设置 sits at the bottom of the nav rail. Example (2026-10-15): the output equalizer leaves 「麦克风与音效」 for a page
  of its own, and doesn't move into 混音器. Keep long explanations short or collapsed, so that controls further down
  stay visible.
- **Bilingual documents** (README, release notes, RELEASING.md): English first, then 简体中文 with the same content.
- **Comments** follow the surrounding code: say why, and date field observations ("2026-10-12: …") that a rule rests on.
- **Commits**: a short subject line (under 60 characters, what changed, e.g. "Network hint: count possible dropouts"),
  a blank line, then the details: GitHub names each CI run after the subject, and a whole paragraph there gets cut
  off. No Co-Authored-By or other attribution lines.
- **Branches**: `dev` is daily work. Other work goes on its own branch with a PR to `dev` (PR builds run CI). `main`
  changes only through the release PR made by `tools/release.ps1`; never push to it. Tags only through
  `tools/release.ps1`; a pushed tag is never moved or deleted. Details: `docs/RELEASING.md`.
- **Releases**: don't ask before publishing a beta. Publish one whenever there is something worth trying on dev and
  dev's CI hasn't failed on it, and tell the maintainer afterwards. `tools/release.ps1 -Beta` releases a snapshot of
  origin/dev from any clone or worktree: it runs CI on `release/snapshot/vX.Y.Z-beta.N` unless dev's CI already passed
  on that commit, so pushes to dev don't interrupt it. A stable 1.0.0 or later needs the maintainer's OK.
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
  folder. Don't edit, build, commit in or remove a worktree the other agent made. The main checkout stays on `dev`
  (hard rules above).
- **Clean up after a merge** (the maintainer, 2026-10-15). As soon as your PR is merged or closed:
  - its branch on GitHub: GitHub deletes it on merge (repository setting since 2026-10-15); for a PR closed without merging, `git push origin --delete <branch>`;
  - remove the worktree: `git worktree remove <path>`;
  - delete the local branch: `git branch -D <branch>`, after checking `git merge-base --is-ancestor <branch> origin/dev`, because `-d` refuses while the main checkout's `dev` is behind.
  Only your own branches and worktrees.
- **The board.** The work items live on the maintainer's private GitHub project board, «HomePodCast» (project 1 of
  qiyinxi: https://github.com/users/qiyinxi/projects/1, since 2026-10-15). Its items are drafts and stay drafts:
  never convert one to an issue, because the repository is public. Read the board with
  `gh project item-list 1 --owner qiyinxi --format json`; the gh token needs the `project` scope.
  - Fields: Status (待办 / 进行中 / 等你决定 / 已完成 / 搁置), 负责人 (Claude / Codex / 维护者), 领域, 优先级, 链接.
  - Item bodies are in Chinese, under the headings 背景 / 需求 / 相关. An item for the other agent also gets a
    `给 Codex 的提示词` (or `给 Claude 的提示词`) section, in English.
  - **Bugs first, and without waiting** (the maintainer, 2026-10-15). A bug is something broken: a regression, a crash
    or a CI failure. Bugs are Claude's, whoever's code they are in. Claude fixes them right away, unless the
    maintainer says not to, and puts them on the board with Claude as 负责人 and the status 进行中.
  - Codex adds the bugs it finds to the board, with 负责人 Claude, and doesn't fix them, unless the maintainer assigns
    one to Codex. Codex still keeps its own open PRs green.
  - **Requirements wait for the maintainer.** A requirement is a new feature, a change of behaviour or an idea. It goes
    on the board first, with no 负责人 and the status 待办. Then ask the maintainer whether Claude or Codex does it,
    and start nothing (no subagents either) until they have assigned it. Work the maintainer asked for in your own
    session counts as assigned. When it's unclear whether something is a bug or a requirement, ask.
  - A question only the maintainer can answer gets the status 等你决定.
  - When you start an assigned item, set it to 进行中. When it is finished, set 已完成 and add the result and links to its
    body.
  - Don't archive items. Views keep finished work apart (2026-10-15):
    - 「待办与进行中」 (table) and 「看板」 (board) hide 已完成 and 搁置;
    - 「等你决定」 lists the questions for the maintainer;
    - 「已完成」 and 「搁置」 hold the rest, one view each.
    The status alone decides which view an item is in.
- **Look before you start.** Run `git fetch` and `gh pr list --state open` and read the board first. If an open PR, a
  fresh branch or a 进行中 item of the other agent covers the same feature or the same files, don't start a parallel
  one: tell the maintainer, and build on it only if they say so.
- **Claim with a draft PR.** Push the branch and open a draft PR to `dev` with your first commit. Its description
  gives the scope and the files it will change, so the other agent can see the area is taken. Mark it ready for review
  when CI is green.
- **Superseding** another agent's PR needs the maintainer's OK. The new PR then says `Supersedes #N` and what it keeps
  and changes, and the old PR gets a comment linking to it. When the new PR is merged, close the old one.
- **Stay close to dev.** Keep PRs small and merge them within a day or two. When `dev` moves, merge `origin/dev` into
  your branch: never rebase a pushed branch, never force-push. Resolve each conflict so that both sides' intent
  survives. If that needs a product decision, ask the maintainer. These files conflict most:
  - `.github/workflows/build.yml`: every new job also goes into the `release` job's `needs`.
  - `src/i18n/*.json`
  - `src/Program.cs` (its commands)
  - `src/UI/Pages/SettingsPage.cs`
  - the READMEs, `docs/device-test-checklist.zh-CN.md` and this file
- **Merging** into `dev` needs no one's OK (the maintainer, 2026-10-14). A PR can be merged once its CI is green and
  it has no conflicts with dev. If dev has since changed code the PR also touches, merge dev in first and let CI run
  again. Merge your own PRs yourself. Review the other agent's PR before you merge it: read the diff, and check that
  the earlier review findings are fixed. If it isn't ready, leave a review comment saying what is missing.
  Drafts are never merged.
- **One release at a time.** `tools/release.ps1 -Beta` refuses while a PR to main is open, a promotion or another tag
  build is running, or a snapshot branch pushed from another checkout exists; `tools/release.ps1 -Status` shows all of
  them. The other agent may be in the middle of one: wait for it, and never take over its snapshot (`-Continue`).
  Before `-Promote`, check the same by hand (`-Status`, `gh pr list --base main`, `gh run list --limit 5`).
- **Plans for the other agent** go in the main checkout's `docs/plans/` (private: excluded through
  `.git/info/exclude`, never committed or pushed). A plan names the branches, worktrees and PR order, and it gets a
  board item that links to it. Progress goes in the PRs and on the board, not in the plan.
- **Real speakers, one agent at a time.** Only the agent the maintainer asked, in its own session, talks to the HomePod
  or any other speaker, even with `stream --silent`. Subagents never do; their tests stay with the in-process fakes.

## Where things are

- `src/Protocol`, `src/Net`: AirPlay 2 (pairing, RTSP, timing, RTP), discovery, takeover rules, network health.
- `src/Audio`: capture (whole output or per app), FIFO, effects. `src/Update`: update check and installer.
- `src/UI`: windows, pages, tray, controls. `src/i18n`: translations.
- `extension/`: the browser extension and its tests (`extension/test/e2e`: the Chrome test).
- `installer/` (WiX MSI), `packaging/msix/` (Store package), `tools/` (release scripts, CI scripts, measuring tools).
- `docs/`: RELEASING.md, the device checklist, feature notes.
