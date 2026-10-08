# HomePodCast MSI installer

`HomePodCast.wxs` describes the Windows Installer package, and `build.ps1` builds it. CI runs the script after `dotnet publish` and attaches `HomePodCast-<version>-x64.msi` to the build artifacts and to GitHub releases, next to the portable zip.

## Building locally

```
dotnet publish src/HomePodCast.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/HomePodCast
./installer/build.ps1 -Version 0.2.0-local.1 -Validate
```

This writes `publish/HomePodCast-0.2.0-local.1-x64.msi`. `-Validate` runs the Windows Installer ICE checks; this works without admin rights. Expect one warning, ICE61: it comes from allowing a build to replace another one with the same version (see below).

Don't install test builds on your everyday PC unless you mean to: the MSI writes to Program Files and adds a firewall rule. To look inside a package without installing it, use `dotnet wix msi decompile <msi> -x <folder>`.

## Toolset

- [WiX Toolset](https://wixtoolset.org/) **5.0.2**, a local dotnet tool pinned in `.config/dotnet-tools.json` (`dotnet tool restore`).
- Extensions `WixToolset.UI.wixext`, `WixToolset.Firewall.wixext` and `WixToolset.Util.wixext`, all **5.0.2**, pinned in `build.ps1`. The script downloads them into `installer/.wix` (git-ignored).
- All of these are under the MS-RL license, and none asks for a license to be accepted. The package embeds WiX's UI, firewall and shell-launch custom-action DLLs.
- Stay on WiX v5. WiX v6 and later require accepting an additional EULA (the Open Source Maintenance Fee).

## What the MSI does

- Installs for all users (per-machine). The default folder is `C:\Program Files\HomePodCast`, and the user can pick another one in the setup wizard. Wizard pages: welcome, license (GPL-3.0, converted from `LICENSE` to RTF at build time), folder, install, finish.
- Files: `HomePodCast.exe` (the framework-dependent single-file build), `LICENSE`, `README*.md`.
- Adds a Start menu shortcut. There is no desktop shortcut: the stock `WixUI_InstallDir` wizard has no place for that option.
- Adds the inbound firewall rule the app would otherwise ask for on first start: `HomePodCast.exe` in the install folder, any protocol, Private profile, local subnet only. It's named "HomePodCast (installer)" and is removed on uninstall. The rule created by the app itself is named "HomePodCast". The two names differ because the WiX firewall action rewrites, and on uninstall deletes, every rule with its name, and it must not touch a rule that a portable copy created. The app looks rules up by program path, so it accepts either one. If the firewall service is off, setup still succeeds.
- Requires the .NET 10 Desktop Runtime (x64). Setup checks for `System.Windows.Forms.dll` 10.x under `shared\Microsoft.WindowsDesktop.App\*` in the x64 .NET location: the registry (`HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\InstallLocation`, 32-bit view) or `C:\Program Files\dotnet`. If the runtime is missing, setup stops and shows https://dotnet.microsoft.com/download/dotnet/10.0.
- The finish page has a "Launch HomePodCast" checkbox, ticked by default. It starts the app as the user who ran setup.
- Upgrades: a newer MSI removes the old version first and then installs the new one. The `UpgradeCode` in `HomePodCast.wxs` ties all versions together and must never change. Installing an older version over a newer one is refused with a message.
- Never touches the settings in `%APPDATA%\HomePodCast`. They stay after an uninstall.

Silent install into a custom folder (from an elevated prompt):

```
msiexec /i HomePodCast-1.2.3-x64.msi /qn INSTALLFOLDER="D:\Apps\HomePodCast"
```

## Version numbers

An MSI version must be numeric (`major.minor.build`, at most 255.255.65535), so `build.ps1` maps the CI version as follows:

| CI version (build.yml) | MSI ProductVersion | File name |
| --- | --- | --- |
| `1.2.3` (tag `v1.2.3`) | `1.2.3` | `HomePodCast-1.2.3-x64.msi` |
| `0.2.0-beta.1` (tag `v0.2.0-beta.1`) | `0.2.0.<run number>` | `HomePodCast-0.2.0-beta.1-x64.msi` |
| `0.2.0-dev.57` (branch build) | `0.2.0.<run number>` | `HomePodCast-0.2.0-dev.57-x64.msi` |
| `0.2.0-pr.12.345` (pull request) | `0.2.0.<run number>` | `HomePodCast-0.2.0-pr.12.345-x64.msi` |

Windows Installer ignores the fourth field when it compares versions. Builds that share `major.minor.build`, such as prereleases, CI builds and the final release, therefore replace each other in either direction (`AllowSameVersionUpgrades`). Only a lower `major.minor.build` counts as a downgrade.

## Signing

The exe has to be signed before it goes into the MSI, and the MSI has to be signed after it is built; see the comments in `.github/workflows/build.yml`. The cabinet is embedded, so signing the `.msi` file is enough (`wix msi inscribe` is only needed for external cabinets).

## Known gaps

- The wizard is English only. Other languages need a `.wxl` localization file and a separate MSI per culture (`wix build -culture zh-CN -loc ...`). The stock WixUI strings already exist for zh-CN, zh-TW and ja. A single multi-language MSI would need language transforms embedded in the package, or a Burn bootstrapper.
- Autostart: the app writes its own `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value. Windows Installer can't clean that up for every user, so after an uninstall a user who had enabled autostart keeps a stale entry, and it does nothing.
- If HomePodCast is running during an upgrade or uninstall, Windows Installer offers to close it. Quitting from the tray first avoids a possible restart prompt.
