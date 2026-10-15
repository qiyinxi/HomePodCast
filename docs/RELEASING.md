# Releasing HomePodCast

[English](#english) | [简体中文](#简体中文)

## English

### The rules

| Branch / tag | What it is |
| --- | --- |
| `dev` | Day-to-day work. Every push runs CI. |
| `vX.Y.Z-beta.N` | A beta: a tag on a `dev` commit whose CI has passed (on `dev` or on its snapshot branch). CI publishes it as a GitHub prerelease. |
| `release/snapshot/vX.Y.Z-beta.N` | A beta's snapshot: the `dev` commit it is made from, while CI runs on it. `tools/release.ps1 -Beta` pushes it and deletes it once the beta is published. |
| `release/X.Y.Z` | The promotion branch: exactly the commit of the beta that becomes `vX.Y.Z`. |
| `main` | Only code verified on a real HomePod. It changes only through the release PR `release/X.Y.Z` → `main`. |
| `vX.Y.Z` | A stable release: a tag on main's merge commit "Release X.Y.Z". CI publishes it as a GitHub release. |

1. **CI passes before a release is tagged.** A tag goes only on a commit whose branch CI run (a push to `dev`, `main` or `release/**`) has already succeeded.
2. **A stable release is the promotion (转正) of a tested beta.** `vX.Y.Z` is built from exactly the source of a `vX.Y.Z-beta.N` that was published as a prerelease and passed `docs/device-test-checklist.zh-CN.md` on the real HomePod. Promotion adds no code; a fix goes into a new beta first. The binaries are built again, since the version is compiled in, but from an identical source tree.
3. **Release notes are English first, then 简体中文.**
4. **A pushed tag is never moved or deleted.** A tag whose build failed keeps its number; the next beta takes the next one.
5. **Approval:** any beta may be published without asking, and any 0.x stable release. A stable 1.0.0 or later needs the maintainer's OK first.

Both flows below are done by `tools/release.ps1` (PowerShell 7, git and an authenticated `gh`; run it from any clone or worktree of the repository). CI enforces rules 1 and 2 for every pushed tag on its own (see [What CI enforces](#what-ci-enforces)), so a tag made by hand can't get around them either. `./tools/release.ps1 -Status` shows where things stand: the version on `dev`, the latest tags, whether `dev`'s CI is green, snapshot branches still around and which betas can be promoted.

The version is `<Version>` in `src/HomePodCast.csproj`. It is set on `dev` in a commit of its own, "Version X.Y.Z", after the previous stable release; every beta and the stable release of X.Y.Z are built from it. CI builds of branches and PRs take it too, as prereleases of X.Y.Z: `X.Y.Z-dev.<run>` for a push to `dev` (other branches by their name likewise) and `X.Y.Z-pr.<n>.<run>` for a PR, with the MSI version X.Y.Z.<run> (see [The installer in CI](#the-installer-in-ci)). Until 2026-10-13 they were `0.2.0-…` whatever the version.

**Experimental features.** A feature first released in a 1.Y.z version is marked experimental in the app, the README and the release notes for the whole 1.Y line. The next minor version, 1.(Y+1).0, drops the label, unless a known problem keeps it; its release notes say which features are no longer experimental. Each label in the code has a `// experimental since 1.Y` comment next to it, so that `git grep "experimental since 1.Y"` finds them all when the minor version goes up. Labels that say a feature has never been tested on real hardware (stereo pairs, multi-room, Apple TV pairing, the home-theater takeover, the wireless cinema) are a different matter: they stay until it has been; `docs/device-test-checklist.zh-CN.md` lists those tests under 「实验性功能」.

### Making a beta

1. Commit on `dev` and push: `git push origin dev`.
2. Write the release notes into a Markdown file (see [Release notes](#release-notes)); the file isn't committed.
3. Dry run: `./tools/release.ps1 -Beta -NotesFile <notes.md> -WhatIf`
4. `./tools/release.ps1 -Beta -NotesFile <notes.md>`

It releases a snapshot of `origin/dev`: the commit `dev` is at when it starts. Pushes to `dev` while it runs go into the next beta and don't interrupt it. (Until 2026-10-15 it waited for `dev`'s own CI run on that commit, and a push to `dev` in the meantime cancelled that run, since build.yml cancels a branch's older run when the branch moves: the beta stopped with "dev's CI ended with cancelled".)

The script:

- takes `origin/dev`'s commit after `git fetch` and reads everything from that commit (`<Version>` through `git show`), not from the working tree, so it runs from any clone or worktree, whatever is checked out there;
- refuses when `vX.Y.Z` exists already, when the commit has a release tag already, and while another release is going on: an open PR to `main`, a promotion branch's CI or another tag build running, or a snapshot branch pushed from another checkout;
- takes N = 1 + the highest `vX.Y.Z-beta.N` here or on origin, counting tags whose build failed;
- tags the commit right away if a push run of `build.yml` has already passed on it (on `dev`, `main` or `release/**`, which is what the release checks accept), and refuses if `dev`'s CI failed on it. Otherwise it pushes the snapshot branch `release/snapshot/vX.Y.Z-beta.N` at the commit and waits for that branch's CI (`gh run watch`). build.yml runs on `release/**`, and each branch has a concurrency group of its own, so pushes to `dev` can't cancel this run;
- creates the annotated tag `vX.Y.Z-beta.N` ("HomePodCast X.Y.Z beta N") on the commit and pushes it;
- waits for the tag build, checks that the release is a prerelease with the `.msi` and the `-win-x64.zip`, and applies the notes (`gh release edit --notes-file`);
- deletes the snapshot branch. The tag keeps the commit, and the snapshot's run stays in the Actions list, where the tag build's release checks found it.

It warns when `main` is not an ancestor of the commit: such a beta can't be promoted (see below). Without `-NotesFile` the release keeps GitHub's generated notes; apply real ones later with `gh release edit vX.Y.Z-beta.N --notes-file <notes.md>`.

**When it stops** (a failed or cancelled run, the network, Ctrl+C), the snapshot branch stays. Running the same command again from the same checkout continues that beta: the snapshot's commit (even if `dev` has moved on since), the same number, and the snapshot's run (a re-run of its jobs is the same run). Its messages say what to do:

- The snapshot's CI failed: if it was flaky infrastructure, `gh run rerun <run id> --failed`, then run the script again. If the code is broken, fix it on `dev`, delete the snapshot (`git push origin --delete release/snapshot/vX.Y.Z-beta.N`) and run the script again: it takes a new snapshot with the same number, since no tag was pushed.
- The snapshot's CI was cancelled (nobody pushes to a snapshot branch, so someone cancelled it, or GitHub did): `gh run rerun <run id>`, then run the script again.
- `dev`'s CI failed on the commit, and there is no snapshot yet: fix it on `dev`. If it was flaky, re-run its failed jobs and run the script again; should a push to `dev` cancel that re-run, the script makes a snapshot instead.
- The tag build failed: the tag stays, and so does the snapshot branch. If it was flaky infrastructure, re-run the failed jobs, then run the script again: it waits for the tag build, checks the release, applies the notes and deletes the snapshot. If the code is broken, delete the snapshot, fix it on `dev` and make the next beta. The snapshot stays after a failed tag build because it tells the next run to finish this beta, rather than start a new one from a `dev` that may have moved on. A beta tagged without a snapshot (its CI had passed on `dev`) is finished by hand, as in [When a tag build fails](#when-a-tag-build-fails).

A snapshot branch pushed from another checkout counts as another release going on: the script refuses, and `-Status` lists it. Each checkout notes the snapshot it pushed in its own git directory (`homepodcast-release-snapshot` in `.git`, or in `.git/worktrees/<name>` for a worktree), so two agents can't finish each other's beta with their own notes. If the checkout that started a beta is gone, `-Beta -Continue` takes its snapshot over. If `dev` has moved on since a snapshot was taken and the new commits belong in this beta, delete the snapshot and run the script again.

**Whose tools.** The steps are those of the `tools/release.ps1` you run, wherever it is. Everything that builds and checks the release (build.yml, `tools/release-checks.ps1`, the installer and package scripts) runs in CI from the tagged commit's own checkout, so it is always the snapshot's own. The script warns when your copy differs from the snapshot's `tools/release.ps1`, so that an old clone gets noticed. `./tools/release.ps1 -SelfTest` checks its decisions offline (snapshot names, where it starts or continues, whether CI must run on a snapshot first, the hints after a failed run, other releases going on); every build runs it.

Then test the beta on the real HomePod with `docs/device-test-checklist.zh-CN.md`. Before that, glance through the screenshots of the GUI smoke test: download the `gui-screenshots` artifact of the beta's tag build (Actions → build → the run → Artifacts) and open its `index.html`. What that job and the other CI jobs and unit tests cover is in the checklist's list 「已自动化（不用手测）」 and needs no testing by hand; of the rest, the short per-release regression (「每版回归」) is done for every beta, and each one-time check (「一次性验证」) only once, and again when the code it names has changed.

### The GUI smoke test

Every build (pushes, PRs, tags) runs the job `gui-smoke` after `build`: `tests/HomePodCast.GuiSmoke` starts the Release exe of that build (from its `-win-x64.zip`) on the runner's desktop with test configs and walks it through UI Automation: every page scrolled through, the tray flyout, 全局快捷键, 使用配置, 多音箱 and 音箱信息; idle, waiting to retry a speaker nobody answers (首页 and the flyout must read 连接), and connected to the tests' fake HomePod on 127.0.0.1:7000; in Chinese, English and German at 100 % (also at the window's minimum width) and 150 % scale, and once in dark mode. It also opens the synchronized viewing window (from the build's `HomePodCast-video-window-*.zip`) from Settings, then on its own with generated 4K pictures through its GPU queue and swap chain, never the window picker. And it fakes a .NET update under the connected app on a private copy of the runner's .NET (a newer patch version appears, the one the app runs on loses its deps.json and System.Net.Http): after 检查更新, 首页 must show the restart notice, and 立即重启 must bring the app back by itself on the newer version, connected again. It fails on the app exiting, an `ERROR` in its log, a window that doesn't appear, controls clipped by or overlapping others, buttons without text, untranslated (Chinese) text in English or German, and its page checks; texts cut off with an ellipsis and controls without an accessible name are listed as warnings. Every screenshot, the UI Automation trees and each run's log are in the `gui-screenshots` artifact; the job summary lists what failed. The `release` job needs it, so a tag whose GUI smoke test fails publishes nothing.

It never runs on a developer's PC: it starts the real app, which would take the HomePod away from the copy in use there. It refuses to start outside GitHub Actions unless given `--allow-local` (only for a machine without a speaker); `HomePodCast.GuiSmoke --selftest` checks the texts it looks for without starting anything.

### Promoting a beta to a stable release

Only a beta that passed the device checklist. `-Status` lists the promotable ones.

1. `./tools/release.ps1 -Status`
2. Write the notes of the stable release (everything since the previous stable release, not only since the last beta).
3. Dry run: `./tools/release.ps1 -Promote vX.Y.Z-beta.N -NotesFile <notes.md> -WhatIf`
4. Run it without `-WhatIf`, preferably from a clean `dev` checkout equal to `origin/dev` (then it also does the last step itself).

The script:

- checks that the beta's prerelease is published and its tag build passed, `vX.Y.Z` doesn't exist, `<Version>` at the beta's commit is X.Y.Z, and `main` is an ancestor of the beta (so the merge adds nothing to the beta's files, and the branch is up to date with `main`, which `main`'s branch protection requires);
- pushes `release/X.Y.Z` at the beta's commit and waits for its CI;
- opens the PR `release/X.Y.Z` → `main` "Release X.Y.Z" (which beta, its release, the device checklist) and waits for its checks;
- merges it with a merge commit whose message is "Release X.Y.Z" (`gh pr merge --merge --subject "Release X.Y.Z" --body "Release X.Y.Z" --match-head-commit <beta>`, the same message as `4aae077` "Release 0.2.1");
- checks that the merge commit's tree is the beta's tree, and refuses to tag it otherwise;
- waits for `main`'s CI on the merge commit, then tags it `vX.Y.Z` ("HomePodCast X.Y.Z") and pushes the tag;
- waits for the tag build, checks that the release is not a prerelease and has the `.msi` and the `-win-x64.zip`, and applies the notes;
- merges `main` back into `dev` and pushes `dev` (see below). From any other checkout it prints the commands instead:
  `git switch dev; git pull --ff-only; git merge --no-ff -m "Merge main after Release X.Y.Z" -m "dev contains main's merge commit again (no file changes)." origin/main; git push origin dev`

Afterwards set the next version on `dev` ("Version X.Y.Z+1" or the next minor) before the next beta; `-Beta` refuses a version that is already released.

**Why `main` goes back into `dev`.** A fast-forward isn't possible: the release merge commit is on `main` only. Without it in `dev`, the next release branch would not be up to date with `main`, which `main`'s branch protection requires before a merge, and updating it would add a commit that no beta was built from. The merge back changes no files (the merge commit's tree is the beta's, and `dev` contains the beta), so it only puts the merge commit into `dev`'s history and keeps `main` an ancestor of `dev`. Before this process the same thing happened implicitly: a feature branch made from `main` brought "Release 0.2.1" into `dev`.

If the script stops on the way (a check failed and was re-run, the network, Ctrl+C), run the same command again: it picks up an existing `release/X.Y.Z` at the beta's commit and an open or merged PR. If it stopped after pushing `vX.Y.Z`, finish by hand: `gh run watch` the tag build, `gh release edit vX.Y.Z --notes-file <notes.md>`, and merge `main` into `dev` as above.

### What CI enforces

For every pushed `v*` tag, the first step of `.github/workflows/build.yml` after the checkout, "Release checks" (`tools/release-checks.ps1`), fails the build, so nothing is published, unless:

1. the tag is `vX.Y.Z`, `vX.Y.Z-beta.N` or `vX.Y.Z-rc.N` and X.Y.Z is `<Version>` in `src/HomePodCast.csproj` at the tagged commit;
2. the tagged commit already has a successful run of this workflow from a push to `dev`, `main` or `release/**` (a beta's snapshot branch `release/snapshot/vX.Y.Z-beta.N` is one; the tag build itself doesn't count);
3. for a stable tag: the commit is on `main`, and a `vX.Y.Z-beta.N` (or `-rc.N`) tag points to a commit with the same source tree and is published (not a draft) as a GitHub prerelease. The log names the matching beta.

The messages it fails with:

- `Tag 'x' is not vX.Y.Z, vX.Y.Z-beta.N or vX.Y.Z-rc.N (N from 1, no leading zeros). …`
- `Tag vX.Y.Z-beta.N is version X.Y.Z, but src/HomePodCast.csproj at <sha> says <Version>…</Version>. Change the version on dev in a commit of its own ('Version X.Y.Z'), push it, wait for its CI to pass, then tag that commit.`
- `Commit <sha> has no successful run of build.yml from a push to dev, main or release/**. Branch runs of this commit: dev run <id> failure. Push the branch, wait for its CI to pass, then tag. If that branch run was still going, re-run this tag build once it has passed; if it failed, fix the code on dev and release the next beta number (never move or delete a pushed tag).`
- `Stable tag vX.Y.Z is on commit <sha>, which is not on main. A stable release is tagged on the merge commit that tools/release.ps1 -Promote makes on main (release/X.Y.Z -> main), after main's CI for it has passed.`
- `Stable tag vX.Y.Z: no published vX.Y.Z-beta.N prerelease has the same source tree as <sha> (tree …). Checked: …. A stable release is a promotion of a tested beta: release this source as a beta first (tools/release.ps1 -Beta), test it, then promote it (tools/release.ps1 -Promote vX.Y.Z-beta.N).`

Branch and PR builds don't run these checks. `main`'s branch protection adds its own: a PR, the checks `build`, `update-e2e`, `msi-e2e`, `msix-e2e`, `extension-e2e` and `gui-smoke`, and the branch up to date with `main`.

The `release` job also needs [the GUI smoke test](#the-gui-smoke-test): nothing is published unless `gui-smoke` passed for the tagged commit too.

The checks read everything through the GitHub API, so they run locally too, read-only:
`./tools/release-checks.ps1 -Tag v0.2.2-beta.4 -Sha 549e83f` (passes), and `./tools/release-checks.ps1 -SelfTest` runs them for known tags of this repository against the results they must give.

Tags made before these rules aren't checked again: re-running one of their workflow runs uses the workflow file of their own commit. By rule 3, `v0.2.1` (no beta) and `v0.2.0` (changes after beta 8) would not pass today; that is expected, and the rule stays strict for every new tag.

### The installer in CI

Every build (push, PR and tag) also runs the job `msi-e2e` after `build`, and the `release` job waits for it, so a tag whose MSI is broken publishes nothing: fix it on `dev` and release the next beta. It replaces the installer part of `docs/device-test-checklist.zh-CN.md` (listed under 「已自动化」 there; 「安装包（MSI）」 keeps what only a person sees). `tools/ci/msi-e2e.ps1`, on the runner:

1. reads the MSI's tables: every language of `installer/build.ps1` is an embedded transform with its own finish-page text, and the zh-CN wizard is Chinese (「启动 HomePodCast」 included) with the license in English;
2. installs it silently into the default folder and checks the files, exactly one Start menu shortcut, exactly one entry in Programs and Features, and exactly one firewall rule for that exe (inbound, allow, Private, LocalSubnet: what the app adds itself); the installed exe's own startup check (`HomePodCast.exe firewall`, read-only) must find it and decide on no firewall prompt;
3. uninstalls it: files, shortcut, firewall rule and Programs entry gone, a seeded `%APPDATA%\HomePodCast` untouched; then the same in a custom `INSTALLFOLDER` with a space and Chinese characters;
4. installs v0.2.0-beta.8, v0.2.0, v0.2.1, v0.2.2, v1.0.0-beta.6, v1.0.0 and the latest release (`gh release download`), one at a time, and the new MSI over each: one HomePodCast afterwards, the new version, same folder, one correct rule, with the two installs in different wizard languages (`TRANSFORMS=:2052`, which the msiexec log must show applied). A branch or PR build is MSI X.Y.Z.<run> with X.Y.Z from the csproj (1.0.0.<run> now), so it really upgrades every release up to X.Y.Z, X.Y.Z itself and its betas included: Windows Installer compares only the first three fields, and the MSI replaces the same version (`AllowSameVersionUpgrades`). A release newer than the build (a beta of the next version, seen from a build of `main`) is checked the other way: the MSI must refuse the downgrade with its message and leave the release installed. Every build also checks that refusal against a copy of its own MSI that says it is the next major version (a newer release as the MSI sees it).

Each check is an `ok` line in the log, and the job summary lists them; when it fails, msiexec's logs are the `msi-e2e-logs` artifact. The script refuses to run outside GitHub Actions; `./tools/ci/msi-e2e.ps1 -Msi <msi> -StaticOnly` runs step 1 anywhere, without installing anything.

### The in-app update in CI

Every build (push, PR and tag) also runs the job `update-e2e` after `build`, and the `release` job waits for it, so a tag whose update path is broken publishes nothing. `tools/ci/update-e2e.ps1`, on the runner:

1. builds an older MSI from the same source (version `0.0.1-e2e.1`, MSI 0.0.1.1; it never leaves the runner) and installs it silently;
2. serves a fake release list in the GitHub API's shape (with the asset's `digest`) and the build's own MSI on `127.0.0.1` (`tools/ci/fake-release-feed.ps1`), so nothing goes to GitHub and the real releases are never touched;
3. runs the installed old exe's `update-selftest --feed http://127.0.0.1:<port>/releases`: the code of 关于 → 软件更新 「更新」 (`src/Update/UpdateFlow.cs`, shared with the button) without UI: check, pick, download, SHA-256, installer helper, exit;
4. checks that the helper ran msiexec (Verb runas), that exactly one HomePodCast is installed and it is the new version, in the same folder, that the new exe was started again with `--updated-from=0.0.1-e2e.1 --tray`, logged "updated from … (msiexec: 0)" and removed its `%TEMP%\HomePodCast-update-*` folder, and that Windows Installer logged the install with status 0;
5. does it for the default folder and for a custom `INSTALLFOLDER` with spaces, and before that serves a corrupted MSI, which must be refused (SHA-256 mismatch: no install, no helper, nothing left in `%TEMP%`).

Its log lines start with `update-e2e:` (each check is a `PASS` line); msiexec's logs, the feed's requests and the app's log are the `update-e2e-logs` artifact. Still for a person (`docs/device-test-checklist.zh-CN.md`: the per-release regression, 「软件更新」 and 「安装包（MSI）」): the UAC dialog itself and declining it (the runner is an administrator without a UAC prompt), SmartScreen on an MSI downloaded with a browser, the portable zip opening the release page, the confirmation and the tray notice, and a check against the real GitHub API.

`update-selftest` takes only an `http://127.0.0.1` or `http://localhost` URL, and only on its command line (no setting, environment variable or config key); downloads come only from that same address, and redirects are not followed. The script refuses to run outside GitHub Actions or where HomePodCast is installed, since it installs and uninstalls the MSI. `./tools/ci/update-e2e.ps1 -NoInstall -Exe <a portable HomePodCast.exe> -NewMsi <any file> -NewVersion 9.9.9-local.1` runs only the parts that install nothing (feed, check, download, SHA-256), anywhere.

### The browser extension in CI

Every build (push, PR and tag) also runs the job `extension-e2e` (Ubuntu, a few minutes, independent of `build`), and the `release` job waits for it. It loads the browser extension unpacked into Chromium (Puppeteer's Chrome for Testing, headless) and drives it on local stand-ins for a YouTube and a bilibili player (also bilibili's WASM `<bwp-video>` canvas player) and a bare `<video>`, against a fake HomePodCast API that the tests switch between streaming, idle and not running (`extension/test/e2e`; host names other than `*.localhost` don't resolve, so nothing leaves the runner and the real sites are never opened). It checks the in-page controls, sync fullscreen, the ±5/±20 ms buttons and Alt shortcuts with the saved site offset and its bounds, the delayed picture through the frame numbers the test video carries (pause, seek, fullscreen, web fullscreen, streaming off), and the popup. Screenshots of failed tests are the `extension-e2e-screenshots` artifact. What it covers is listed under 「已自动化」 in `docs/device-test-checklist.zh-CN.md`; the real sites, lip sync and 60 fps smoothness stay with a person (「浏览器插件」 there).

To run it locally: `npm ci`, then `npm test` in `extension/test/e2e` (ffmpeg makes the test video). When HomePodCast holds port 47100, the fake API takes another port and the test uses a copy of the extension that asks that port; `HPC_E2E_HEADED=1` shows the browser.

### The Store package in CI

Every build (push, PR and tag) also runs the job `msix-e2e` after `build`, and the `release` job waits for it, so a tag whose Microsoft Store package fails there publishes nothing. `packaging/msix/e2e-test.ps1` installs a copy of the `msix` artifact signed with a throwaway certificate (the artifact itself stays unsigned, as the Store wants it), starts it the way Start does, checks package identity, the log in the package's storage, that it never contacts GitHub, the manifest's firewall rules and no firewall prompt, the single-instance lock against a second start and the MSI build, and the startup task (through the app's `autostart on|off|status` command, the code of its switch), then uninstalls it and checks that nothing is left. Each check is a `PASS` line in the job's log; the app's log is the `msix-e2e-logs` artifact. What it covers is listed under 「已自动化」 in `docs/device-test-checklist.zh-CN.md`, and 「Microsoft Store 版」 there keeps the rest; the details are in `packaging/msix/README.md` (CI test). The script refuses to run outside GitHub Actions unless given `-Force` (a throwaway test VM): it installs a certificate, the package and the MSI.

### Release notes

- English first, then 简体中文 under a `## 简体中文` heading, with the same content. `release.ps1` warns when the notes file has no Chinese or starts with it.
- A beta: what changed since the previous beta, what to test, known problems.
- A stable release: everything since the previous stable release, what was verified on the real HomePod, known problems.
- Mention updates when they matter: the MSI updates itself from 关于 → 软件更新; the portable zip and the Store version don't.

```markdown
## Highlights
- …

## Fixes
- …

## Known issues
- …

## 简体中文

### 亮点
- …

### 修复
- …

### 已知问题
- …
```

### When a tag build fails

The tag stays: never move or delete a pushed tag, not even one whose build failed. Find the run with `gh run list --branch <tag>`.

- **"Release checks" failed because the tag was pushed before the branch CI had finished**: when the branch run passes, re-run the tag build (`gh run rerun <run id>`); it then publishes normally, and the number isn't lost. If the branch run failed, fix it on `dev` and make the next beta.
- **Flaky infrastructure** (a runner or network hiccup, a NuGet outage, a timing test on a busy runner, a GUI smoke step that waited too long for a window): `gh run rerun <run id> --failed`. If the beta has a snapshot branch (it stays after a failed tag build), run `./tools/release.ps1 -Beta -NotesFile <notes.md>` again from the same checkout: it waits for the tag build, checks the release, applies the notes and deletes the snapshot. Otherwise `release.ps1` stopped at the failure, so check the release and apply the notes by hand afterwards (`gh release view <tag>`, `gh release edit <tag> --notes-file <notes.md>`).
- **The code is broken**: delete the beta's snapshot branch if it has one (`git push origin --delete release/snapshot/vX.Y.Z-beta.N`), fix it on `dev`, push, and make the next beta (`-Beta` takes the next number). The failed tag keeps its number without a release; that's what happened to `v0.2.2-beta.2` and `beta.3` on 2026-10-11 (tagged before `dev`'s CI had finished, which then failed); `beta.4` was the first good one.
- **A stable tag build fails**: its source is a beta whose build passed, so it is almost always infrastructure: re-run the failed jobs. Should the code really be at fault, `vX.Y.Z` is spent: fix it on `dev`, set the next patch version, and release that as a beta, then promote it.

## 简体中文

### 规则

| 分支 / 标签 | 含义 |
| --- | --- |
| `dev` | 日常开发。每次推送都跑 CI。 |
| `vX.Y.Z-beta.N` | 测试版：打在 CI 已经通过（在 `dev` 上或在它的快照分支上）的 `dev` 提交上的标签。CI 把它发布成 GitHub 预发布版本。 |
| `release/snapshot/vX.Y.Z-beta.N` | 测试版的快照：这个测试版所用的 `dev` 提交，在它的 CI 运行期间存在。`tools/release.ps1 -Beta` 推送它，测试版发布后删掉它。 |
| `release/X.Y.Z` | 转正分支：正好是要转正成 `vX.Y.Z` 的那个测试版的提交。 |
| `main` | 只放在真实 HomePod 上验证过的代码。只通过发布 PR `release/X.Y.Z` → `main` 改变。 |
| `vX.Y.Z` | 正式版：打在 main 的合并提交 "Release X.Y.Z" 上的标签。CI 把它发布成 GitHub 正式版本。 |

1. **先过 CI，再打发布标签。** 标签只能打在分支 CI（推送到 `dev`、`main` 或 `release/**` 时跑的那次）已经成功的提交上。
2. **正式版是已测试测试版的转正。** `vX.Y.Z` 必须正好用某个 `vX.Y.Z-beta.N` 的源码构建，而这个测试版已经作为预发布版本发布，并在真实 HomePod 上过了 `docs/device-test-checklist.zh-CN.md`。转正时不加任何代码；修复要先进新的测试版。安装包会重新构建（版本号编译在程序里），但源码树完全相同。
3. **发布说明先英文，后简体中文。**
4. **推送过的标签永远不移动、不删除。** 构建失败的标签也占着它的编号，下一个测试版用下一个编号。
5. **批准：** 测试版（任何版本号）和 0.x 正式版可以直接发布；1.0.0 及以后的正式版要先经维护者同意。

下面两个流程都由 `tools/release.ps1` 完成（需要 PowerShell 7、git 和已登录的 `gh`；在这个仓库的任何克隆或工作树里运行都行）。CI 对每个推送的标签另外强制执行第 1、2 条（见「[CI 强制检查什么](#ci-强制检查什么)」），所以手动打的标签也绕不过去。`./tools/release.ps1 -Status` 显示当前情况：`dev` 上的版本、最新的标签、`dev` 的 CI 是否通过、还留着的快照分支、哪些测试版可以转正。

版本号是 `src/HomePodCast.csproj` 里的 `<Version>`。上一个正式版发布后，在 `dev` 上用一个单独的提交 "Version X.Y.Z" 设置；X.Y.Z 的所有测试版和正式版都用它构建。分支和 PR 的 CI 构建也用它，作为 X.Y.Z 的预发布版本：推送到 `dev` 是 `X.Y.Z-dev.<运行号>`（其他分支同样用分支名），PR 是 `X.Y.Z-pr.<PR 号>.<运行号>`，MSI 版本是 X.Y.Z.<运行号>（见「[CI 里的安装包测试](#ci-里的安装包测试)」）。2026-10-13 之前它们不管版本号是多少都是 `0.2.0-…`。

**实验性功能。** 在 1.Y.z 版本里第一次发布的功能，在整个 1.Y 系列里都在程序、README 和发布说明里标成「实验性」。到下一个次版本 1.(Y+1).0 去掉这个标注，除非还有已知问题；1.(Y+1).0 的发布说明要写明哪些功能不再是实验性的。代码里每处标注旁边都加一条 `// experimental since 1.Y` 注释，升次版本时用 `git grep "experimental since 1.Y"` 就能全部找到。因为「还没在真实设备上测过」而标的实验性（立体声对、多房间、Apple TV 配对、家庭影院临时接管、无线影院）是另一回事：要等实机测过才去掉；要测的项在 `docs/device-test-checklist.zh-CN.md` 的「实验性功能」里。

### 发测试版

1. 在 `dev` 上提交并推送：`git push origin dev`。
2. 把发布说明写进一个 Markdown 文件（见「[发布说明](#发布说明)」），这个文件不提交。
3. 演练：`./tools/release.ps1 -Beta -NotesFile <说明.md> -WhatIf`
4. `./tools/release.ps1 -Beta -NotesFile <说明.md>`

它发布的是 `origin/dev` 的快照：脚本开始时 `dev` 所在的那个提交。脚本运行期间推到 `dev` 的提交进下一个测试版，不会打断它。（2026-10-15 之前它等的是 `dev` 自己在这个提交上的 CI，而这期间只要有人推送 `dev`，那次运行就会被取消，因为 build.yml 在分支前进时取消这个分支上旧的运行：测试版就停在「dev's CI ended with cancelled」。）

脚本会：

- `git fetch` 之后取 `origin/dev` 的提交，所有内容都从这个提交读取（`<Version>` 用 `git show` 读），不读工作区，所以在任何克隆或工作树里都能运行，不管那里检出的是什么；
- 在这些情况下拒绝：`vX.Y.Z` 已经存在；这个提交已经有发布标签；还有别的发布正在进行：有打开的到 `main` 的 PR、转正分支的 CI 或另一个标签构建正在运行、或者有从另一个检出推送的快照分支；
- 取 N = 本地或 origin 上最大的 `vX.Y.Z-beta.N` + 1，构建失败的标签也算；
- 如果这个提交已经有一次 `build.yml` 的推送运行通过了（在 `dev`、`main` 或 `release/**` 上，也就是发布检查认可的那些），就直接打标签；如果 `dev` 的 CI 在它上面失败了，就拒绝。否则把快照分支 `release/snapshot/vX.Y.Z-beta.N` 推送到这个提交上，等这个分支的 CI（`gh run watch`）。build.yml 对 `release/**` 也会运行，而且每个分支有自己的并发组，所以推送 `dev` 取消不了这次运行；
- 在这个提交上创建附注标签 `vX.Y.Z-beta.N`（"HomePodCast X.Y.Z beta N"）并推送；
- 等标签构建完成，检查发布是预发布版本、带 `.msi` 和 `-win-x64.zip`，然后应用说明（`gh release edit --notes-file`）；
- 删掉快照分支。标签留住了这个提交，快照的那次运行也留在 Actions 列表里，标签构建的发布检查就是在那里找到它的。

如果 `main` 不是这个提交的祖先，脚本会警告：这样的测试版不能转正（见下文）。不给 `-NotesFile` 时，发布页保留 GitHub 自动生成的说明，之后用 `gh release edit vX.Y.Z-beta.N --notes-file <说明.md>` 换成正式的。

**脚本中途停下时**（运行失败或被取消、网络问题、Ctrl+C），快照分支会留着。在同一个检出里再运行一次同样的命令，就会接着做这个测试版：用快照的提交（即使 `dev` 之后又前进了）、同一个编号、快照已有的那次运行（重跑它的作业还是同一次运行）。它的提示会说明该怎么做：

- 快照的 CI 失败：如果是基础设施偶发问题，`gh run rerun <运行 id> --failed`，然后再运行脚本。如果是代码有问题，在 `dev` 上修好，删掉快照（`git push origin --delete release/snapshot/vX.Y.Z-beta.N`），再运行脚本：因为还没有推送标签，它会用同一个编号重新取快照。
- 快照的 CI 被取消（没有人会推送快照分支，所以是有人手动取消的，或者是 GitHub 取消的）：`gh run rerun <运行 id>`，然后再运行脚本。
- `dev` 的 CI 在这个提交上失败，而且还没有快照：在 `dev` 上修好。如果是偶发问题，重跑它失败的作业，再运行脚本；如果这次重跑又被推送 `dev` 取消了，脚本会改用快照。
- 标签构建失败：标签保留，快照分支也保留。如果是基础设施偶发问题，重跑失败的作业，然后再运行脚本：它会等标签构建、检查发布、应用说明，再删掉快照。如果是代码有问题，删掉快照，在 `dev` 上修好，发下一个测试版。标签构建失败后快照还留着，是为了让下一次运行把这个测试版做完，而不是从可能已经前进了的 `dev` 开始一个新的测试版。没有快照就打了标签的测试版（它的 CI 已经在 `dev` 上通过），按「[标签构建失败时](#标签构建失败时)」手动收尾。

从另一个检出推送的快照分支算作另一个正在进行的发布：脚本会拒绝，`-Status` 会列出它。每个检出把自己推送的快照记在自己的 git 目录里（`.git` 里的 `homepodcast-release-snapshot`，工作树则在 `.git/worktrees/<名字>` 里），这样两个代理不会用各自的说明去完成对方的测试版。如果开始这个测试版的那个检出已经没了，用 `-Beta -Continue` 接手它的快照。如果取快照之后 `dev` 又前进了，而新的提交应该进这个测试版，就删掉快照再运行脚本。

**用的是谁的工具。** 步骤来自你运行的那份 `tools/release.ps1`，不管它在哪里。构建和检查发布的一切（build.yml、`tools/release-checks.ps1`、安装包和商店包的脚本）都在 CI 里从打标签的那个提交自己的检出运行，所以总是快照自己的。你的副本和快照里的 `tools/release.ps1` 不同时，脚本会警告，这样旧的克隆会被注意到。`./tools/release.ps1 -SelfTest` 离线检查它的判断（快照分支的名字、从哪里开始或接着做、是否要先在快照上跑 CI、运行失败后的提示、别的发布是否正在进行）；每次构建都会运行它。

然后用 `docs/device-test-checklist.zh-CN.md` 在真实 HomePod 上测试这个测试版。在那之前先扫一眼界面冒烟测试的截图：下载这个测试版标签构建的 `gui-screenshots` 工件（Actions → build → 这次运行 → Artifacts），打开里面的 `index.html`。这个作业和其他 CI 作业、单元测试覆盖的，列在清单的「已自动化（不用手测）」里，不用再手动测；其余的，「每版回归」每个测试版都做，「一次性验证」只测一次，它写明的代码改了才重测。

### 界面冒烟测试

每次构建（推送、PR、标签）在 `build` 之后都会跑作业 `gui-smoke`：`tests/HomePodCast.GuiSmoke` 在运行器的桌面上用测试配置启动这次构建的 Release 版程序（来自它的 `-win-x64.zip`），通过 UI Automation 走一遍：每一页整页滚动，托盘面板，全局快捷键、使用配置、多音箱和 音箱信息；没有音箱、等待重试一个没人应答的音箱地址（首页和托盘面板必须显示「连接」）、连着测试用的假 HomePod（127.0.0.1:7000）三种状态；中文、English、Deutsch，在 100 %（另加最小窗口宽度）和 150 % 缩放下，另有一次深色模式。它还会从设置打开同步观影窗口（来自这次构建的 `HomePodCast-video-window-*.zip`），再单独用生成的 4K 画面运行它，经过显卡队列和交换链显示，从不打开窗口选择器。它还在运行器上 .NET 的私有副本上模拟一次推流中的 .NET 更新（出现更新的补丁版本，程序用的版本没了 deps.json 和 System.Net.Http）：点「检查更新」后首页必须出现重启提示，点「立即重启」后程序必须自己在新版本上重新打开、重新连上。程序退出、日志里有 `ERROR`、窗口没出来、控件被裁掉或互相重叠、按钮没有文字、English 或 Deutsch 里有没翻译的中文，以及各页的检查没通过，都会让它失败；被省略号截断的文字、没有无障碍名称的控件作为警告列出。所有截图、UI Automation 树和每次运行的日志都在 `gui-screenshots` 工件里，作业摘要列出失败项。`release` 作业依赖它，所以界面冒烟测试失败的标签不会发布任何东西。

它不会在开发者的电脑上跑：它启动的是真程序，会把那台电脑上正在用的 HomePod 抢走。不在 GitHub Actions 里时它拒绝启动，除非加 `--allow-local`（只用于没有音箱的机器）；`HomePodCast.GuiSmoke --selftest` 只检查它要找的界面文字，什么都不启动。

### 测试版转正

只转正过了实机测试清单的测试版。`-Status` 会列出可以转正的。

1. `./tools/release.ps1 -Status`
2. 写正式版的发布说明（上一个正式版以来的全部变化，而不只是上一个测试版以来的）。
3. 演练：`./tools/release.ps1 -Promote vX.Y.Z-beta.N -NotesFile <说明.md> -WhatIf`
4. 去掉 `-WhatIf` 运行，最好在一个干净、和 `origin/dev` 一致的 `dev` 检出里运行（这样最后一步也由脚本完成）。

脚本会：

- 检查：这个测试版的预发布版本已发布、标签构建通过；`vX.Y.Z` 还不存在；测试版提交里的 `<Version>` 是 X.Y.Z；`main` 是这个测试版的祖先（这样合并不会给测试版的文件加任何东西，分支也和 `main` 保持最新，这是 `main` 的分支保护要求的）；
- 在测试版的提交上推送 `release/X.Y.Z`，等它的 CI；
- 开 PR `release/X.Y.Z` → `main`，标题 "Release X.Y.Z"（写明是哪个测试版、它的发布页、实机测试清单），等检查通过；
- 用合并提交合并，提交信息是 "Release X.Y.Z"（`gh pr merge --merge --subject "Release X.Y.Z" --body "Release X.Y.Z" --match-head-commit <测试版>`，和 `4aae077` "Release 0.2.1" 一样）；
- 检查合并提交的树和测试版的树相同，不同就拒绝打标签；
- 等 `main` 上这个合并提交的 CI，然后打标签 `vX.Y.Z`（"HomePodCast X.Y.Z"）并推送；
- 等标签构建完成，检查发布不是预发布版本、带 `.msi` 和 `-win-x64.zip`，然后应用说明；
- 把 `main` 合并回 `dev` 并推送 `dev`（原因见下）。在其他检出里运行时，脚本改为打印这些命令：
  `git switch dev; git pull --ff-only; git merge --no-ff -m "Merge main after Release X.Y.Z" -m "dev contains main's merge commit again (no file changes)." origin/main; git push origin dev`

之后在 `dev` 上设置下一个版本号（"Version X.Y.Z+1" 或下一个次版本），再发下一个测试版；`-Beta` 会拒绝已经发布过的版本号。

**为什么要把 `main` 合并回 `dev`。** 不能快进：发布用的合并提交只在 `main` 上。`dev` 里没有它的话，下一个 release 分支就不是基于最新的 `main`，而 `main` 的分支保护要求合并前分支是最新的；更新分支又会多出一个没有任何测试版用它构建过的提交。合并回去不改任何文件（合并提交的树就是测试版的树，而 `dev` 包含这个测试版），只是把合并提交放进 `dev` 的历史，让 `main` 始终是 `dev` 的祖先。在有这个流程之前，这件事是隐式发生的：一个从 `main` 拉出的功能分支把 "Release 0.2.1" 带进了 `dev`。

如果脚本中途停下（某项检查失败后重跑、网络问题、Ctrl+C），再运行一次同样的命令即可：它会接着用已在测试版提交上的 `release/X.Y.Z` 和已经打开或已合并的 PR。如果是在推送 `vX.Y.Z` 之后停下的，就手动收尾：`gh run watch` 标签构建，`gh release edit vX.Y.Z --notes-file <说明.md>`，再按上面的命令把 `main` 合并回 `dev`。

### CI 强制检查什么

每推送一个 `v*` 标签，`.github/workflows/build.yml` 在检出代码后的第一步「Release checks」（`tools/release-checks.ps1`）会让构建失败、什么都不发布，除非：

1. 标签是 `vX.Y.Z`、`vX.Y.Z-beta.N` 或 `vX.Y.Z-rc.N`，并且 X.Y.Z 等于该提交里 `src/HomePodCast.csproj` 的 `<Version>`；
2. 这个提交已经有一次这个工作流的成功运行，来自推送到 `dev`、`main` 或 `release/**`（测试版的快照分支 `release/snapshot/vX.Y.Z-beta.N` 也算；标签构建本身不算）；
3. 正式版标签还要求：提交在 `main` 上，并且有一个 `vX.Y.Z-beta.N`（或 `-rc.N`）标签指向源码树相同的提交，而且它已作为 GitHub 预发布版本发布（不是草稿）。日志里会写出匹配的是哪个测试版。

失败时的提示就是上面英文部分列出的那几条（CI 日志是英文的）。

分支和 PR 构建不跑这些检查。`main` 的分支保护另外要求：通过 PR；`build`、`update-e2e`、`msi-e2e`、`msix-e2e`、`extension-e2e`、`gui-smoke` 这几项检查通过；分支和 `main` 保持最新。

`release` 作业还依赖[界面冒烟测试](#界面冒烟测试)：打标签的提交 `gui-smoke` 也通过了才会发布。

这些检查全部通过 GitHub API 读取，所以也可以在本地只读地运行：
`./tools/release-checks.ps1 -Tag v0.2.2-beta.4 -Sha 549e83f`（通过）；`./tools/release-checks.ps1 -SelfTest` 用本仓库已知的标签运行检查，并和应得的结果对比。

有这些规则之前打的标签不会再被检查：重跑它们的工作流时，用的是它们自己提交里的工作流文件。按第 3 条，`v0.2.1`（没有测试版）和 `v0.2.0`（beta 8 之后还有改动）今天都过不了；这是预期的，规则对每个新标签都严格执行。

### CI 里的安装包测试

每次构建（推送、PR 和标签）在 `build` 之后还会跑 `msi-e2e` 作业，`release` 作业要等它通过，所以 MSI 坏了的标签什么都不会发布：在 `dev` 上修好，发下一个测试版。它代替 `docs/device-test-checklist.zh-CN.md` 里安装包的部分（列在那里的「已自动化」里；「安装包（MSI）」一节只留只有人能看到的）。`tools/ci/msi-e2e.ps1` 在运行器上：

1. 读 MSI 的表：`installer/build.ps1` 的每种语言都是一个嵌入的转换，完成页文字是它自己的；zh-CN 的向导是中文（包括「启动 HomePodCast」），许可协议是英文；
2. 静默安装到默认目录，检查程序文件、开始菜单里正好一个快捷方式、「程序和功能」里正好一个条目、正好一条指向这个 exe 的防火墙规则（入站、允许、专用网络、本地子网，和程序自己添加的一样）；装好的 exe 自己的启动检查（`HomePodCast.exe firewall`，只读）要找到它、判断不弹防火墙提示；
3. 卸载：程序文件、快捷方式、防火墙规则和「程序和功能」里的条目都没了，预先放好的 `%APPDATA%\HomePodCast` 原样保留；然后在带空格和中文的自定义 `INSTALLFOLDER` 里再来一遍；
4. 逐个安装 v0.2.0-beta.8、v0.2.0、v0.2.1、v0.2.2、v1.0.0-beta.6、v1.0.0 和最新发布的版本（`gh release download`），再在上面装新 MSI：之后只有一个 HomePodCast，是新版本、目录不变、规则只有一条且正确，新旧两次安装用不同的向导语言（`TRANSFORMS=:2052`，msiexec 日志里要能看到转换已应用）。分支或 PR 构建的 MSI 版本是 X.Y.Z.<运行号>，X.Y.Z 取自 csproj（现在是 1.0.0.<运行号>），所以它能真正升级 X.Y.Z 及以前的所有发布，包括 X.Y.Z 本身和它的测试版：Windows Installer 只比较前三段，相同版本由 MSI 替换（`AllowSameVersionUpgrades`）。比这次构建新的发布（比如从 `main` 的构建看，下一个版本的测试版）反过来检查：MSI 必须拒绝降级、给出提示、旧版原样保留。每次构建还会用自己 MSI 的一个副本（改成下一个主版本号，也就是 MSI 眼里更新的发布）检查这种拒绝。

日志里每项检查是一行 `ok`，作业摘要里也列出所有检查；失败时 msiexec 的日志在 `msi-e2e-logs` 产物里。脚本在 GitHub Actions 之外拒绝运行；`./tools/ci/msi-e2e.ps1 -Msi <msi> -StaticOnly` 只做第 1 步，在哪里都能跑，不安装任何东西。

### CI 里的软件更新测试

每次构建（推送、PR 和标签）在 `build` 之后还会跑 `update-e2e` 作业，`release` 作业要等它通过，所以更新流程坏了的标签什么都不会发布。`tools/ci/update-e2e.ps1` 在运行器上：

1. 用同一份源码构建一个更旧的 MSI（版本 `0.0.1-e2e.1`，MSI 版本 0.0.1.1；它不会离开运行器），并静默安装；
2. 在 `127.0.0.1` 上提供一个假的发布列表（和 GitHub API 的格式一样，带安装包的 `digest`）和这次构建自己的 MSI（`tools/ci/fake-release-feed.ps1`），所以不会访问 GitHub，也绝不会碰真实的发布；
3. 运行已安装旧版的 `update-selftest --feed http://127.0.0.1:<端口>/releases`：就是 关于 → 软件更新 「更新」 的代码（`src/Update/UpdateFlow.cs`，和按钮共用），只是没有界面：检查、选版本、下载、SHA-256 校验、启动安装助手、退出；
4. 检查：助手运行了 msiexec（Verb runas）；只装了一个 HomePodCast，是新版本，目录没变；新版 exe 以 `--updated-from=0.0.1-e2e.1 --tray` 重新启动，日志里有「updated from … (msiexec: 0)」，并删掉了 `%TEMP%\HomePodCast-update-*` 文件夹；Windows Installer 记录的安装结果是 0；
5. 默认目录和一个带空格的自定义 `INSTALLFOLDER` 各测一遍；在这之前先提供一个损坏的 MSI，它必须被拒绝（SHA-256 不符：不安装、不启动助手、`%TEMP%` 里不留东西）。

它的日志行以 `update-e2e:` 开头（每项检查一行 `PASS`）；msiexec 的日志、假发布列表收到的请求和程序日志在 `update-e2e-logs` 产物里。仍然需要人来测的（`docs/device-test-checklist.zh-CN.md` 的「每版回归」「软件更新」和「安装包（MSI）」）：UAC 弹窗本身以及在里面点「否」（运行器是管理员，UAC 不弹窗）、用浏览器下载的 MSI 的 SmartScreen 提示、便携版 zip 打开发布页、确认框和托盘提示、对真实 GitHub API 的检查。

`update-selftest` 只接受 `http://127.0.0.1` 或 `http://localhost` 的地址，而且只能写在它自己的命令行上（没有设置项、环境变量或配置键）；只从同一个地址下载，不跟随重定向。这个脚本会安装和卸载 MSI，所以在 GitHub Actions 之外、或者本机已经装了 HomePodCast 时会拒绝运行。`./tools/ci/update-e2e.ps1 -NoInstall -Exe <便携版 HomePodCast.exe> -NewMsi <任意文件> -NewVersion 9.9.9-local.1` 只跑不安装任何东西的部分（假发布列表、检查、下载、SHA-256），在哪里都能跑。

### CI 里的浏览器插件测试

每次构建（推送、PR 和标签）还会跑 `extension-e2e` 作业（Ubuntu，几分钟，不依赖 `build`），`release` 作业也要等它通过。它把浏览器插件以解压方式加载进 Chromium（Puppeteer 自带的 Chrome for Testing，无头模式），在本地仿制的 YouTube、bilibili 播放器页面（也有 bilibili 的 WASM 播放器 `<bwp-video>`）和一个裸 `<video>` 页面上操作，连的是假的 HomePodCast 本地接口，测试中会把它切换成推流中、未推流和没在运行（`extension/test/e2e`；`*.localhost` 以外的主机名都解析不了，所以不会访问运行器以外的地址，也绝不会打开真实网站）。它检查页面上的插件按钮、同步全屏、±5／±20 ms 按钮和 Alt 快捷键（含保存的网站偏移和上下限）、按测试视频每帧里的帧号检查延迟画面（暂停、拖进度条、全屏、网页全屏、停止推流），以及插件弹窗。失败的测试会留下截图，在 `extension-e2e-screenshots` 产物里。它覆盖的项目列在 `docs/device-test-checklist.zh-CN.md` 的「已自动化」里；真实网站、口型和 60 帧流畅度仍要人测（那里的「浏览器插件」）。

本地运行：在 `extension/test/e2e` 里 `npm ci`，再 `npm test`（用 ffmpeg 生成测试视频）。HomePodCast 占着 47100 端口时，假接口换一个端口，测试用一份改成这个端口的插件副本；设 `HPC_E2E_HEADED=1` 可以看到浏览器。

### CI 里的 Microsoft Store 版测试

每次构建（推送、PR 和标签）在 `build` 之后还会跑 `msix-e2e` 作业，`release` 作业要等它通过，所以 Microsoft Store 版的包在那里没通过的标签什么都不会发布。`packaging/msix/e2e-test.ps1` 安装 `msix` 产物的一个副本（用一次性证书签名；产物本身仍然不签名，Store 要的就是不签名的包），像开始菜单那样启动它，检查包标识、日志在包的存储里、从不访问 GitHub、清单里的防火墙规则和没有防火墙提示、对再次启动和 MSI 版的单实例锁、开机启动任务（用程序的 `autostart on|off|status` 命令，也就是开关的那段代码），然后卸载并检查什么都没留下。每项检查在作业日志里是一行 `PASS`；程序日志在 `msix-e2e-logs` 产物里。覆盖了哪些项，列在 `docs/device-test-checklist.zh-CN.md` 的「已自动化」里，其余的在那里的「Microsoft Store 版」；细节见 `packaging/msix/README.md` 的 CI test 一节。这个脚本会安装证书、包和 MSI，所以在 GitHub Actions 之外不加 `-Force`（一次性的测试虚拟机）就拒绝运行。

### 发布说明

- 先英文，然后在 `## 简体中文` 标题下写同样内容的中文。说明文件里没有中文或以中文开头时，`release.ps1` 会警告。
- 测试版：和上一个测试版相比改了什么、要测什么、已知问题。
- 正式版：上一个正式版以来的全部变化、在真实 HomePod 上验证了什么、已知问题。
- 需要时说明更新方式：MSI 安装版可以在 关于 → 软件更新 里自己更新；便携版 zip 和商店版不会。

模板见英文部分。

### 标签构建失败时

标签保留：推送过的标签永远不移动、不删除，构建失败的也一样。用 `gh run list --branch <标签>` 找到那次运行。

- **「Release checks」失败，因为标签在分支 CI 跑完之前就推送了**：分支那次运行通过后，重跑标签构建（`gh run rerun <运行 id>`），它会正常发布，编号不会浪费。如果分支那次运行失败了，就在 `dev` 上修好，发下一个测试版。
- **基础设施偶发问题**（运行器或网络抖动、NuGet 故障、繁忙运行器上的计时测试、界面冒烟测试等窗口时超时）：`gh run rerun <运行 id> --failed`。如果这个测试版有快照分支（标签构建失败后它会留着），之后在同一个检出里再运行一次 `./tools/release.ps1 -Beta -NotesFile <说明.md>`：它会等标签构建、检查发布、应用说明，再删掉快照。否则 `release.ps1` 在失败处已经停下，之后要手动检查发布并应用说明（`gh release view <标签>`、`gh release edit <标签> --notes-file <说明.md>`）。
- **代码有问题**：如果这个测试版有快照分支，先删掉它（`git push origin --delete release/snapshot/vX.Y.Z-beta.N`），然后在 `dev` 上修好、推送，再发下一个测试版（`-Beta` 会自动用下一个编号）。失败的标签占着编号，没有发布页；2026-10-11 的 `v0.2.2-beta.2` 和 `beta.3` 就是这样（在 `dev` 的 CI 跑完之前打了标签，而那次 CI 失败了），`beta.4` 才是第一个好的。
- **正式版标签构建失败**：它的源码就是一个构建通过的测试版，所以几乎一定是基础设施问题：重跑失败的作业。万一真是代码问题，`vX.Y.Z` 这个号就用掉了：在 `dev` 上修好，设置下一个补丁版本号，先作为测试版发布，再转正。
