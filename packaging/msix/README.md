# HomePodCast MSIX package (Microsoft Store)

`build.ps1` builds `HomePodCast-<version>-x64.msix` from `AppxManifest.xml` (a template), the logos in `Assets/` and `priconfig.xml`. CI builds it on every run and uploads it as the workflow artifact **msix**. It is not attached to GitHub releases: the package is unsigned, and an unsigned MSIX only installs from the Store, which signs it with a Microsoft certificate when it publishes it. GitHub users keep getting the MSI and the zip.

Release plan:

| Tag | GitHub release | Microsoft Store |
| --- | --- | --- |
| `vX.Y.Z-beta.N` | MSI + zip (prerelease) | no (the MSIX is only a workflow artifact) |
| `vX.Y.Z` | MSI + zip | the MSIX, through the `store` job in `.github/workflows/build.yml` (dormant for now, see [Automatic Store submissions](#automatic-store-submissions)) |

## Building locally

Needs the .NET 10 SDK and the Windows 10/11 SDK (for `makeappx.exe` and `makepri.exe`; Visual Studio's "Desktop development with C++" workload installs it, and so does the standalone Windows SDK installer). The script picks the x64 tools of the newest SDK under `Windows Kits\10\bin`; `-SdkBinDir` points it somewhere else.

```
./packaging/msix/build.ps1 -Version 1.0.0-local.1
```

This writes `publish/HomePodCast-1.0.0-local.1-x64.msix` (about 47 MB, 270 files, mostly the .NET runtime) in under a minute once the runtime packs are in the NuGet cache. Work files stay in `packaging/msix/obj` (git-ignored); the unpacked package is `packaging/msix/obj/layout`. The build doesn't touch `app/` or `src/obj`.

What the script does:

1. `dotnet publish` self-contained for win-x64, as a plain folder (not single-file). The Store doesn't install the .NET Desktop Runtime, so the package carries it; the MSI and the zip stay framework-dependent.
2. Copies the logos and `LICENSE`, and writes `AppxManifest.xml` with the identity, display names and version filled in.
3. `makepri new` indexes the logos into `resources.pri` (see [Logos](#logos)).
4. `makeappx pack` validates the manifest against the schema, checks every file it names and packs the folder. Any error stops the build.

### Trying a build

Don't install test packages on your everyday PC unless you mean to: a packaged HomePodCast and an MSI or portable one share the single-instance lock, so only one of them runs at a time. To try the package on a test PC without signing it, turn on Developer Mode (Settings > System > For developers) and register the unpacked folder:

```
Add-AppxPackage -Register packaging\msix\obj\layout\AppxManifest.xml
```

Remove it again with `Get-AppxPackage qiyinxi.HomePodCast | Remove-AppxPackage`. What to check in a registered package is in `docs/device-test-checklist.zh-CN.md`, section 十一. Installing the `.msix` file itself needs a signature from a certificate whose subject is exactly the `Publisher` below and which the PC trusts (for example a self-signed one in Trusted People, signed with `signtool sign /fd SHA256`).

[Windows App Certification Kit](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-app-certification-kit) (`appcert.exe` in the Windows SDK) runs the Store's local checks; it installs the package to test it, so run it on a test PC too.

## Package identity

Partner Center assigned these when the name HomePodCast was reserved (Product management > Product identity). They are public, and `build.ps1` has them as defaults; the Store refuses a package whose values differ in any way, case included.

| Manifest value | Value | `build.ps1` parameter | Repository variable (overrides) |
| --- | --- | --- | --- |
| `Package/Identity/Name` | `qiyinxi.HomePodCast` | `-IdentityName` | `MSIX_IDENTITY_NAME` |
| `Package/Identity/Publisher` | `CN=874AAE1B-1F37-40C7-80D9-3146C953705B` | `-Publisher` | `MSIX_PUBLISHER` |
| `Package/Properties/PublisherDisplayName` | `qiyinxi` | `-PublisherDisplayName` | `MSIX_PUBLISHER_DISPLAY_NAME` |
| `Package/Properties/DisplayName` | `HomePodCast` (must be a name reserved for the app) | `-DisplayName` | |

The Store ID (product id) is **9N9FTNR9HBG2** (https://apps.microsoft.com/detail/9N9FTNR9HBG2 once the app is published).

## Version numbers

| CI version (build.yml) | Package version | Store |
| --- | --- | --- |
| `1.2.3` (tag `v1.2.3`) | `1.2.3.0` | submitted by the `store` job |
| `1.3.0-beta.1` (tag `v1.3.0-beta.1`) | `1.3.0.0` | no, artifact only |
| `0.2.2` (tag `v0.2.2`) | `0.2.2.0` | no: the Store refuses a first field of 0 |
| `0.2.0-dev.57`, `0.2.0-pr.12.345` | `0.2.0.0` | no, artifact only |

The Store reserves the fourth field (it must be 0 in what you upload) and refuses packages whose first field is 0, so **the first Store release has to be 1.0.0 or later**. `build.ps1` warns about a 0.x version, and the `store` job skips 0.x tags. Every package submitted must have a higher version than the one in the Store, which stable tags give naturally.

## Manifest

- **Full-trust desktop app**: `EntryPoint="Windows.FullTrustApplication"` with the restricted capability `runFullTrust`. The app runs as a normal Win32 process, not in an AppContainer: global hotkeys, the low-level keyboard hook for the volume keys, process loopback capture, COM and raw sockets all work as in the MSI build. Partner Center asks why the app needs `runFullTrust`; answer that it is a Win32 (WinForms) desktop app.
- **Capabilities**:
  - `runFullTrust` (required, see above);
  - `microphone` (`DeviceCapability`): enforced for packaged apps. Mic monitoring needs it, and the user can turn it off for HomePodCast under Settings > Privacy & security > Microphone;
  - `privateNetworkClientServer`: declarative only for a full-trust app (it neither grants nor blocks anything); it tells Store users that the app uses the home network (AirPlay 2, mDNS, and the browser extension's API on 127.0.0.1:47100);
  - no `internetClient`: the package makes no internet requests; the Store build gets its updates from the Store. Add it if that changes;
  - no `allowElevation`: a packaged build must not start the elevated PowerShell that adds the firewall rule (the manifest's rules replace it).
- **Firewall**: two package-level `windows.firewallRules` rules for `HomePodCast.exe`, inbound, Private profile, one for TCP and one for UDP. Windows adds them when the package is installed, without a UAC prompt, and removes them on uninstall. The MSI's rule is "any protocol, Private, local subnet"; the manifest schema has one protocol per rule and no remote-address scope, so the "local subnet only" restriction can't be expressed. What comes in unasked is UDP (the speaker's timing and retransmit requests, mDNS answers) on ports picked at run time, so the rules have no port ranges.
- **Minimum Windows**: `Windows.Desktop` 10.0.19041.0 (Windows 10 version 2004), the first build with process loopback capture, which per-app audio routing uses. `MaxVersionTested` is 10.0.26100.0.
- **Languages**: the 15 UI languages the app has (en-US first as the package default, then zh-CN, zh-TW, ja-JP, de-DE, fr-FR, es-ES, it-IT, nl-NL, pl-PL, pt-PT, sv-SE, da-DK, nb-NO, fi-FI). The app picks its language at run time either way; the declaration is what the Store shows as supported languages. Partner Center lists them on the app overview under "Languages supported by your packages" and recommends a Store listing for each. A Store listing isn't required for every one: remove the languages you don't want to write a listing for there (the Store then doesn't show them either), or write the listings and import them as CSV. To declare fewer languages, edit `<Resources>` in `AppxManifest.xml`.
- **Autostart**: a `windows.startupTask` extension on the app (TaskId `HomePodCastStartup`, off until the user turns it on) with `uap10:Parameters="--tray"` (Windows 10 2004+), so a start at sign-in goes to the tray like the unpackaged app's HKCU `Run` value, which doesn't work in a package (HKCU writes go to the package's private hive). The app's 开机自动启动 switch calls the `StartupTask` API through its WinRT ABI (`src/StartupTask.cs`, no Windows SDK projection, so the target framework and the MSI and zip stay as they are); Settings > Apps > Startup and Task Manager show the same switch. If the user turned it off there, only they can turn it on again there, and the app offers to open the page. Should Windows not pass the parameter, the app still sees that the startup task started it (`AppInstance.GetActivatedEventArgs`) and starts in the tray. The task is declared on the app itself rather than on a hidden second application with `AppListEntry="none"`, so the window opened from the tray keeps HomePodCast's own taskbar entry and notification identity.

## Logos

`Assets/` holds the logos the manifest names: `Square44x44Logo` (app list, taskbar, Start, Explorer), `Square150x150Logo` and `Wide310x150Logo` (Windows 10 Start tiles) and `StoreLogo` (package logo), each in scales 100, 125, 150, 200 and 400, and the 44x44 logo also in target sizes 16 to 256, plated and unplated (the same picture: the icon brings its own blue plate). They are generated, not drawn by hand:

```
python tools/make_icon.py --msix
```

`make_icon.py` draws the icon from the same shapes as `docs/icon.svg` and `docs/icon-small.svg` (Pillow; 16 and 20 px use the small variant) and writes every file again. The icon logos are the rounded tile, like the exe's icon; the Start tiles are blue edge to edge with the icon's shapes in the middle. `priconfig.xml` makes `makepri` index them into a single `resources.pri`, so Windows can pick the right file; it is the `makepri createconfig` default without the split into resource packs, which only an app bundle uses.

Partner Center also takes Store logos for the listing; `python tools/make_icon.py --png store-logo.png --size 1080` draws one at any size.

## One-time Partner Center steps

1. **Reserve the name** (done: HomePodCast, Store ID 9N9FTNR9HBG2) and copy the identity values from Product identity into `build.ps1` (done).
2. **First submission by hand** (the API can only update an app that is already in the Store):
   - Pricing and availability: free; markets.
   - Properties: category (Music, or Utilities & tools), privacy policy URL `https://github.com/qiyinxi/HomePodCast/blob/main/PRIVACY.md`, support contact `https://github.com/qiyinxi/HomePodCast/issues`.
   - Age ratings: the IARC questionnaire (no user interaction, no shared location, no purchases).
   - Packages: the `msix` artifact of a stable build with version 1.0.0 or later.
   - Store listings: description, at least one screenshot, per language you keep (see [Manifest](#manifest), Languages).
   - Submission options, notes for certification: the app needs an AirPlay 2 speaker (HomePod) on the same network to stream anything; `runFullTrust` because it is a Win32 desktop app; the microphone is only used for the optional mic monitoring, which is off at every start; the low-level keyboard hook is installed only while streaming, so that the keyboard's volume keys change the speaker's volume, and acts on those keys only. Say in the description that HomePodCast is not affiliated with Apple (HomePod and AirPlay are Apple trademarks).
3. **API access for CI**:
   - Associate a Microsoft Entra ID tenant with the Partner Center account (Account settings > Tenants), or create one there.
   - Register an application in that tenant (Entra admin center > App registrations) and create a client secret for it. Secrets expire: note the date and renew it, and the repository secret, before then.
   - In Partner Center, Account settings > User management > Microsoft Entra applications: add the application with the **Manager** role.
   - Note the tenant ID, the application (client) ID, the secret and the Seller ID (Partner Center, Account settings).
4. **Enable the CI step**: add the secrets below, then uncomment the `store` job at the end of `.github/workflows/build.yml`.

## Automatic Store submissions

The `store` job in `.github/workflows/build.yml` is commented out until the steps above are done. Once enabled, it runs for stable tags (no `-` in the version) of 1.0.0 or later, after the GitHub release: it downloads the `msix` artifact, installs the Microsoft Store Developer CLI with `microsoft/microsoft-store-apppublisher` (formerly `microsoft/setup-msstore-cli`), signs in with `msstore reconfigure` and runs `msstore publish <msix> -id <product id>`. That creates a new submission from the last published one with the new package and commits it to certification; add `--noCommit` to the command to check it in Partner Center first. Listing changes are still made in Partner Center (or with `msstore submission updateMetadata`).

| Name | Kind | Value |
| --- | --- | --- |
| `PARTNER_CENTER_TENANT_ID` | secret | Entra tenant ID |
| `PARTNER_CENTER_CLIENT_ID` | secret | Entra application (client) ID |
| `PARTNER_CENTER_CLIENT_SECRET` | secret | the application's client secret |
| `PARTNER_CENTER_SELLER_ID` | secret | Partner Center seller ID |
| `MSSTORE_PRODUCT_ID` | variable, optional | Store ID; the job uses `9N9FTNR9HBG2` when it isn't set |
| `MSIX_IDENTITY_NAME`, `MSIX_PUBLISHER`, `MSIX_PUBLISHER_DISPLAY_NAME` | variables, optional | override the identity defaults in `build.ps1` (see [Package identity](#package-identity)) |

## How the Store version differs

- **Self-contained**: the package includes the .NET 10 runtime (about 47 MB instead of a few MB); runtime fixes arrive with app updates.
- **Per-user install** into `C:\Program Files\WindowsApps\<package>\`, read-only, a new folder for every version. No admin rights, no install-folder choice.
- **Updates come from the Store.** The app's own update check must stay off in the Store build (Store policy).
- **Firewall**: the manifest's rules replace the UAC prompt the app shows otherwise (Private networks, any remote address on them; see [Manifest](#manifest)). Windows stores them with the exe's path in the package's version folder under WindowsApps (rule group `{78E1CD88-49E3-476E-B926-580E596AD309}`). The app never offers its own rule when packaged (`src/AppEnvironment.cs` says whether it is): it counts the manifest's rules as there even when it can't match one to its exe, still warns about a Public network, and when the speaker is blocked on a Private network it says that another firewall or security program is the likely cause (one that allows programs by path has to allow the new path after every update) or that the rules were deleted (reinstalling adds them back).
- **Settings and log**: Windows redirects the package's writes under `%APPDATA%` to the package's own storage (`%LOCALAPPDATA%\Packages\qiyinxi.HomePodCast_21rkx5qfj0b6t\LocalCache\Roaming\HomePodCast`; the family name is the identity name plus a hash of the publisher), so `%APPDATA%\HomePodCast` in Explorer doesn't show what the Store version wrote, and uninstalling removes it. Files that already existed there (from an MSI or portable copy) are read and changed in place. Settings → Tools → Log → Open log folder opens whichever folder holds the log.
- **Microphone**: the per-app switch under Settings > Privacy & security > Microphone applies. When Windows refuses the microphone (E_ACCESSDENIED, in either version), the app stops retrying, says so once and offers to open that page.
- **Autostart**: the startup task (see [Manifest](#manifest)); the `Run` value has no effect, and the packaged app neither writes nor repairs it.
- **Restart after a change of the UI language**: through the package (`explorer.exe shell:AppsFolder\qiyinxi.HomePodCast_21rkx5qfj0b6t!HomePodCast`), so the new copy has package identity too.
- **No command line**: the `scan`, `info`, `stream` and other commands are not reachable (there is no app execution alias), and Start launches the app without arguments.
- The Store version and an MSI or portable copy don't run at the same time: they share the single-instance lock.
