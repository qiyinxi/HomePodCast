# HomePodCast MSI installer

`HomePodCast.wxs` describes the Windows Installer package, `i18n/*.wxl` hold its strings in each language, and `build.ps1` builds it. CI runs the script after `dotnet publish` and attaches `HomePodCast-<version>-x64.msi` to the build artifacts and to GitHub releases, next to the portable zip. It is a single MSI whose setup wizard follows the Windows display language (see [Languages](#languages)).

## Building locally

```
dotnet publish src/HomePodCast.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/HomePodCast
./installer/build.ps1 -Version 0.2.0-local.1 -Validate
```

This writes `publish/HomePodCast-0.2.0-local.1-x64.msi` and takes about a minute. The per-language packages and transforms it is made from stay in `installer/obj`. `-Validate` works without admin rights and checks two things:

- The Windows Installer ICE checks on the final MSI and on the package of every other language. Expect one ICE61 warning per package (15 in all): it comes from allowing a build to replace another one with the same version (see below).
- For each language transform embedded in the MSI, it applies the transform to a copy, the same way Windows Installer does at install time, and checks that the result is identical to that language's own package (`Database.GenerateTransform` finds no differences). It also checks that the finish-page checkbox text matches the `.wxl`.

Don't install test builds on your everyday PC unless you mean to: the MSI writes to Program Files and adds a firewall rule. To look inside a package without installing it, use `dotnet wix msi decompile <msi> -x <folder>`. The decompiled MSI shows the English base. For another language, decompile `installer/obj/<culture>/HomePodCast.msi`.

## Toolset

- [WiX Toolset](https://wixtoolset.org/) **5.0.2**, a local dotnet tool pinned in `.config/dotnet-tools.json` (`dotnet tool restore`).
- Extensions `WixToolset.UI.wixext`, `WixToolset.Firewall.wixext` and `WixToolset.Util.wixext`, all **5.0.2**, pinned in `build.ps1`. The script downloads them into `installer/.wix` (git-ignored).
- All of these are under the MS-RL license, and none asks for a license to be accepted. The package embeds WiX's UI, firewall and shell-launch custom-action DLLs.
- Stay on WiX v5. WiX v6 and later require accepting an additional EULA (the Open Source Maintenance Fee).
- Embedding the language transforms uses the Windows Installer COM object (`WindowsInstaller.Installer`), which is part of Windows. It needs no Windows SDK and no admin rights.

## Languages

The wizard is available in 15 languages:

| Culture | LCID | | Culture | LCID | | Culture | LCID |
| --- | --- | --- | --- | --- | --- | --- | --- |
| en-US (base) | 1033 | | fr-FR | 1036 | | pt-PT | 2070 |
| zh-CN | 2052 | | es-ES | 3082 | | sv-SE | 1053 |
| zh-TW | 1028 | | it-IT | 1040 | | da-DK | 1030 |
| ja-JP | 1041 | | nl-NL | 1043 | | nb-NO | 1044 |
| de-DE | 1031 | | pl-PL | 1045 | | fi-FI | 1035 |

How it works (the usual way to make one multilingual MSI):

1. `build.ps1` builds `HomePodCast.wxs` once per culture (`wix build -culture <culture> -loc i18n/<culture>.wxl`). The packages are identical apart from their strings, `ProductLanguage` and codepage. All of them get the same product code, generated once per build.
2. For every culture except en-US, `wix msi transform -t language` makes a transform from the en-US package to that culture's package.
3. The output is the en-US package with every transform embedded as a sub-storage named after its LCID (`2052`, `1041`, …). The language list in the summary information (Template `x64;1033,2052,1028,…`) names them all.

When the MSI starts, Windows Installer applies the embedded transform that matches the user's Windows display language. If no language matches, the wizard is in English. To choose a language explicitly, pass the transform: `msiexec /i HomePodCast-1.2.3-x64.msi TRANSFORMS=:2052`.

Strings:

- The stock wizard dialogs (welcome, folder, progress, finish, errors) use the translations in `WixToolset.UI.wixext`. Version 5.0.2 covers all 15 cultures above, and 25 more (among them ko-KR, ru-RU, pt-BR and zh-HK).
- Our own strings are in `i18n/<culture>.wxl`: the "Launch HomePodCast" checkbox, the missing .NET 10 runtime message, the downgrade message, the Start menu shortcut's description and the firewall rule's description. The rule's name stays "HomePodCast (installer)" in every language, because uninstall finds the rule by that name.
- `WixToolset.Firewall.wixext` 5.0.2 translates its progress and error messages only into en-US, es-ES, ja-JP and pl-PL. The other `.wxl` files therefore add those seven strings.
- The license page always shows the GPL-3.0 in English. That is the license's official text, and the FSF's translations are unofficial.
- `MajorUpgrade` has `IgnoreLanguage="yes"`, so an upgrade finds the installed version whatever language it was installed in, including the earlier English-only MSIs.

To add a language:

1. Copy `i18n/en-US.wxl` to `i18n/<culture>.wxl`. Set `Culture`, and set `Codepage` to the culture's ANSI codepage (the one in the stock WixUI strings). Then translate the strings.
2. If `WixToolset.Firewall.wixext` has no strings for that culture, add its seven firewall strings as well. If any are missing, the build stops with "localization variable … is unknown".
3. Add the culture and its LCID to `$Cultures` in `build.ps1`.

Strings are MSI formatted text, so write a literal `[` or `]` as `[\[]` or `[\]]`.

## What the MSI does

- Installs for all users (per-machine). The default folder is `C:\Program Files\HomePodCast`, and the user can pick another one in the setup wizard. Wizard pages: welcome, license (GPL-3.0, converted from `LICENSE` to RTF at build time), folder, install, finish.
- Files: `HomePodCast.exe` (the framework-dependent single-file build), `LICENSE`, `README*.md`.
- Adds a Start menu shortcut. There is no desktop shortcut: the stock `WixUI_InstallDir` wizard has no place for that option.
- Adds the inbound firewall rule the app would otherwise ask for on first start: `HomePodCast.exe` in the install folder, any protocol, Private profile, local subnet only. It's named "HomePodCast (installer)" and is removed on uninstall. The rule created by the app itself is named "HomePodCast". The two names differ because the WiX firewall action rewrites, and on uninstall deletes, every rule with its name, and it must not touch a rule that a portable copy created. The app looks rules up by program path, so it accepts either one. If the firewall service is off, setup still succeeds.
- Requires the .NET 10 Desktop Runtime (x64). Setup checks for `System.Windows.Forms.dll` 10.x under `shared\Microsoft.WindowsDesktop.App\*` in the x64 .NET location: the registry (`HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\InstallLocation`, 32-bit view) or `C:\Program Files\dotnet`. If the runtime is missing, setup stops and shows https://dotnet.microsoft.com/download/dotnet/10.0.
- The finish page has a "Launch HomePodCast" checkbox, ticked by default. It starts the app as the user who ran setup.
- Upgrades: a newer MSI removes the old version first and then installs the new one, whichever languages the two were installed in. The `UpgradeCode` in `HomePodCast.wxs` ties all versions together and must never change. Installing an older version over a newer one is refused with a message.
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

The exe has to be signed before it goes into the MSI, and the MSI has to be signed after `build.ps1` has finished, because the script changes the file when it embeds the language transforms. See the comments in `.github/workflows/build.yml`. The cabinet and the transforms are embedded, so signing the `.msi` file is enough (`wix msi inscribe` is only needed for external cabinets).

## Known gaps

- The license page is in English for every language (see [Languages](#languages)).
- Autostart: the app writes its own `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value. Windows Installer can't clean that up for every user, so after an uninstall a user who had enabled autostart keeps a stale entry, and it does nothing.
- If HomePodCast is running during an upgrade or uninstall, Windows Installer offers to close it. Quitting from the tray first avoids a possible restart prompt.
