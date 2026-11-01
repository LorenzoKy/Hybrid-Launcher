# Development

## Layout

```
src/HybridLauncher.Core     engine, no UI: detection, accounts, downloads, cloud saves, updates, OptiScaler bat driver, ini editor
src/HybridLauncher          WinUI 3 app (unpackaged, self-contained); the exe is HybridLauncher.exe
src/HybridLauncher.Starter  the small exe at the top of the published folder; it starts app\HybridLauncher.exe
tests/HybridLauncher.Smoke  console checks against real Windows and network, one command per area
data/                       files the installed apps read from this repository (tech-versions.json)
docs/                       user and developer documentation
site/                       the website, published to GitHub Pages by .github/workflows/pages.yml
.github/                    issue templates and workflows
```

Inside `src/HybridLauncher`: `Views/` (pages), `Controls/` (reusable controls), `Helpers/` (UI services: sign-in, cloud
sync, updater, tray, motion), `Assets/` (logo, icons, font). Inside `src/HybridLauncher.Core`: one folder per area
(`Accounts`, `Cloud`, `Detection`, `Hardware`, `Media`, `Mods`, `Opti`, `Steam`, …).

## The published folder

```
HybridLauncher.exe          starter: starts app\HybridLauncher.exe with the same arguments
HybridLauncher.exe.config
app\                            the app, .NET and the Windows App SDK (self-contained)
  HybridLauncher.exe
  Assets\  en-us\  en-GB\  it-IT\  Microsoft.UI.Xaml\  …
```

Publishing keeps only the Windows App SDK language folders the app uses (English and Italian; the target
`DropUnusedLanguageFolders` in `HybridLauncher.csproj`). Shortcuts, *Start with Windows* and updates use the real exe in
`app\`; the starter is only what a person double-clicks.

## Build

Requirements: Windows 10/11, the .NET 10 SDK. The Windows SDK build tools and the Windows App SDK come from NuGet.

```bash
dotnet build src/HybridLauncher/HybridLauncher.csproj -c Release -p:Platform=x64
```

Release builds carry no path of the build PC (`src/Directory.Build.props` maps them to `/_/`).

## Run on test data

`HL_DATA=<folder>` makes the app use another data folder instead of `%LOCALAPPDATA%\HybridLauncher`; such an instance
runs beside the normal one. Put a `settings.json` there with `"Language": "en"` to test in English.

- `HybridLauncher.exe --selftest` opens every game, tab, view, page and theme, then closes; the log's last line counts the
  failures. `HL_SHOTS=<folder>` saves a picture of the window after every step, `HL_SELFTEST_ONLY=downloads,settings` runs
  only the steps starting with those words, `HL_SAMPLE_DOWNLOADS=1` adds pretend downloads.
- `HL_SYSTEM_FORMAT=<culture>` pretends Windows' regional format is another one (`hi-IN`, `th-TH`, `ar-SA`): the self-test
  should pass under any of them, since the app always runs in en-US, en-GB or it-IT (`L.Formats`).
- `HL_UPDATE_FEED=<file or URL>` replaces GitHub's `releases/latest` for the update check (a JSON in GitHub's format).

## Smoke tests

```bash
cd tests/HybridLauncher.Smoke
dotnet run -c Release -- <command>
```

The list of commands is at the top of `Program.cs`: `decide`, `ini <file>`, `scan`, `hw`, `cloud`, `updates`, `signin-page`,
`art`, `install <folder> nightly|custom`, `modinstall`, `steamsession`, and more. Commands that sign in use the tokens under
`HL_DATA`; run them on a scratch copy.

## Making a release

The app updates itself from GitHub Releases, so a release must follow these rules:

1. Set `<Version>` in `src/HybridLauncher/HybridLauncher.csproj` (major.minor.patch) and add the version to `CHANGELOG.md`.
2. Publish self-contained into `app`, and put the starter beside it:

   ```bash
   dotnet publish src/HybridLauncher/HybridLauncher.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o dist/HybridLauncher-<version>-win-x64/app
   dotnet build src/HybridLauncher.Starter/HybridLauncher.Starter.csproj -c Release -o dist/starter -p:Version=<version>
   ```

   Copy `HybridLauncher.exe` and `HybridLauncher.exe.config` from `dist/starter` into
   `dist/HybridLauncher-<version>-win-x64`, then zip that folder's contents (starter and `app` at the zip's root), named
   `HybridLauncher-<version>-win-x64.zip` (`-r win-arm64 -p:Platform=ARM64` and `-win-arm64.zip` for ARM). The updater
   runs the starter it finds in the zip, which hands over to the new app.
3. Create a GitHub release with the tag `v<version>` (for example `v1.0.0`), the changelog entry as its notes, and the zip(s)
   as assets. Do not mark it draft or pre-release, or the app will not offer it.

The app picks the asset whose name ends in `.zip` and contains its processor's runtime (`win-x64` / `win-arm64`), so keep
those names.
4. Before releasing, run the self-test **several times** (crashes that show up 1 time in 10 hide in a single run).

## Technology versions (My PC)

`src/HybridLauncher.Core/Hardware/TechVersions.cs` keeps the newest version of every upscaler and frame generation
technology. It starts from a built-in table, then once a day reads the vendors' SDK releases on GitHub
(`GPUOpen-LibrariesAndSDKs/FidelityFX-SDK` release notes, `intel/xess` tags and notes, `NVIDIA/DLSS` tags) and
[`data/tech-versions.json`](../data/tech-versions.json) from this repository's `main` branch. Between the built-in table and
the vendors the highest version wins; the correction file has the last word, so it can also lower a wrong value.

Edit `data/tech-versions.json` for what the vendors do not publish in a readable way (NVIDIA's own numbering such as DLSS 4.5,
Reflex, AFMF, Anti-Lag) or to fix a wrong value: every installed app picks it up within a day, without a new release. Ids:
`fsr-upscaling-ml`, `fsr-framegen-ml`, `fsr-ray-regeneration`, `fsr-upscaling`, `fsr-framegen`, `xess`, `xell`, `dlss-sdk`,
`dlss`, `dlss-neural`, `dlss-framegen-rtx40`, `reflex`, `afmf`, `anti-lag`. Check the readers with
`dotnet run -- techversions` in the smoke tests.

## Conventions

- Every visible text goes through `L.T` / `L.F`; the Italian table is `src/HybridLauncher.Core/Localization/Italian.cs`.
- No personal paths or machine-specific values in code or assets.
- The app's former name appears only in `AppInfo.LegacyName`, used to carry a user's data over.
