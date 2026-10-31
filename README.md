<p align="center">
  <img src="docs/images/logo.png" width="128" alt="Hybrid Launcher logo: a line-drawn pizza slice in white with red pepperoni rings on a dark rounded square" />
</p>

<h1 align="center">Hybrid Launcher</h1>

<p align="center">
  <b>The Hybrid Launcher: manage your games,<br />manage your mods, enhance the experience.</b><br />
  Steam · Epic Games · GOG · Xbox / Game Pass · your own folders
</p>

<p align="center">
  <a href="https://github.com/LorenzoKy/Hybrid-Launcher/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/LorenzoKy/Hybrid-Launcher?include_prereleases&label=download&color=D7143A" /></a>
  <img alt="Windows 11, 64-bit" src="https://img.shields.io/badge/Windows-11%20%C2%B7%2064--bit-2A2E38" />
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-555" /></a>
  <a href="https://lorenzoky.github.io/Hybrid-Launcher/"><img alt="Website" src="https://img.shields.io/badge/website-lorenzoky.github.io-555" /></a>
</p>

<p align="center">
  <a href="https://ko-fi.com/lorenzoky"><img alt="Support me on Ko-fi" src="https://ko-fi.com/img/githubbutton_sm.svg" /></a>
</p>

<p align="center">
  <a href="#at-a-glance">At a glance</a> ·
  <a href="#features">Features</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="#faq">FAQ</a> ·
  <a href="#reporting-bugs-and-requesting-features">Report a bug</a> ·
  <a href="#support-the-project">Support</a> ·
  <a href="#disclaimer">Disclaimer</a>
</p>

---

## At a glance

Hybrid Launcher is a free and open source Windows 11 app that finds the games installed on your PC, gives each one its own page, and helps
you install a few popular mods that make them run and look better: **OptiScaler**, **ReShade**, **RenoDX** and **Luma**.

|  |  |
|---|---|
| **One library** | Steam, Epic Games, GOG, Xbox / Game Pass and your own folders, in one list |
| **Mods, the official way** | Each mod through its own installer (OptiScaler's `setup_windows.bat`, ReShade's setup), answered for you |
| **Launcher features** | Play time, cloud saves, Smart HDR, achievements, direct downloads from your store accounts |
| **Yours to shape** | Tabs, launch bar, banner, groups and colours can be toggled, dragged and hidden |
| **Portable** | One zip, no installer; settings in `%LOCALAPPDATA%\HybridLauncher` |

It is *hybrid* in two ways: it brings several stores into one library, and it mixes a launcher (play, download, update, cloud
saves) with a modding tool (upscalers, frame generation, HDR mods, shaders).

### Why this project?

Modern PC gaming is getting overwhelming for anyone who wants the best experience out of their games. The best tools are
external programs and mods, each with its own installer, its own folders and its own rules: many paths to remember, create
and manage, spread over half a dozen launchers. Hybrid Launcher brings some of the main mods and the usual launcher features
together, and offers them as **one alternative**.

> [!IMPORTANT]
> **This is not a beginner-first app.** It is meant for people with some basic experience. Get familiar with the mods it
> handles, read their wikis, and understand how they affect your game and your game folder: you will get much more out of
> it, and you will know what is happening when something does not work.

## Features

### Your games

- **One library.** Steam, Epic Games, GOG and Xbox / Game Pass, plus any exe or games folder you add. Covers, play time,
  achievements, pins, search, filters, controller support.
- **A page per game.** Play / Stop, last played and play time, trailer and store details, reviews, HowLongToBeat,
  PCGamingWiki fixes, links and notes.
- **Direct downloads.** Sign in to Steam (QR code or password), Epic or GOG to list your library and download games from
  Hybrid Launcher (some games may still need their store's own app to run). Pause and resume, and updates that download only
  what changed and are designed to leave your mods in place.
- **Cloud saves.** Your saves synced to **OneDrive** (through Windows' OneDrive app), **MEGA**, a folder of your choice, or
  more than one at once, with history, restore of older copies and conflict handling.

### Mods

- **OptiScaler, made easy.** A quick panel for the settings you change most, configuration profiles, and new versions
  fetched from GitHub by themselves. Custom builds are supported too, and every version is saved, listed and one click away.
- **ReShade, RenoDX, Luma.** ReShade's own installers (with or without add-on support, loadable through OptiScaler), RenoDX
  HDR mods matched to the game when one exists, Luma mods. Removal keeps your presets.
- **Backup or replace.** For the game files a mod replaces: back them up first (the default), just replace them, or be asked
  at each install.
- **Smart HDR.** Turn it on for a game and Windows HDR switches **on when the game starts and off when it closes**.

### Extras

- **Custom achievements.** Rarity, a platinum trophy at 100 %, and an **achievement pop-up you design**: position, screen,
  size, colour, background, opacity, time on screen, sound.
- **Quick access.** Your own shortcuts per game: folders, tools, config files, one click away from the game page.
- **My PC.** Your graphics card, processor, memory and drives at a glance, displays and Windows' gaming switches, and the versions of the upscalers and frame generation your graphics driver provides.
- **And more.** Desktop shortcuts, start with Windows, tray mode, a controller-friendly window, a welcome tour, self-updates
  with one confirmation, and a one-click bug report.

Every feature is explained in the [user guide](docs/user-guide.md).

## Quick start

1. **Download** `HybridLauncher-<version>-win-x64.zip` from [Releases](https://github.com/LorenzoKy/Hybrid-Launcher/releases/latest).
2. **Extract** it anywhere you can write, for example `C:\Games\Hybrid Launcher`.
3. **Run** `HybridLauncher.exe`. Nothing else to install: .NET and the Windows App SDK ship inside the `app` folder.
4. **Pick a game**, open its OptiScaler or ReShade & Mods tab, and press Install.

**Requirements:** Windows 11 (or Windows 10 with the latest updates), 64-bit.

**Where things go:** the folder holds `HybridLauncher.exe` to start the app and `app\` with the app and its runtime. Your
settings live in `%LOCALAPPDATA%\HybridLauncher`; apart from that, the app writes only into the game folders you ask it to
change and the cloud places you pick for your saves.

**Updates:** the app checks for a new version a few seconds after it starts and asks once before updating. See
[docs/updates.md](docs/updates.md).

## FAQ

<details>
<summary><b>Is it free?</b></summary>

Yes. It is free and open source software under the GNU GPL v3, see the [license](#license).
</details>

<details>
<summary><b>Can I build it myself, change it or fork it?</b></summary>

Yes. The source code is all here: read it, build it, test it on your PC, send fixes, fork it. A fork or unofficial build you
share must keep the GPL v3 and credit this project, see the [license](#license).
</details>

<details>
<summary><b>Do I need to sign in to my stores?</b></summary>

No. Without accounts it reads the games your launchers installed on this PC. Signing in only adds direct downloads and your
full owned library.
</details>

<details>
<summary><b>Can I go back to the original game files?</b></summary>

Yes, when the backup is on (the default): removing a mod puts back the files it replaced.
</details>

<details>
<summary><b>Can mods get me banned?</b></summary>

They can, in games and services that forbid them. Check the rules of every game before you mod it, and never use mods in
online or competitive games that do not allow them.
</details>

More answers in [FAQ and troubleshooting](docs/faq.md).

## Reporting bugs and requesting features

> [!WARNING]
> **Please read this before opening an issue.**
>
> Hybrid Launcher is a **personal passion project**. The time I can spend on it is very limited, so **updates come at
> random times and are not guaranteed**. What helps it keep going:
>
> - people to work with (see [how to get in touch](SUPPORT.md)),
> - maintainers and contributors (see [CONTRIBUTING.md](CONTRIBUTING.md)),
> - [support for the project](#support-the-project).
>
> Please respect this choice. **Reports and requests are only considered when the description is detailed, understandable
> and makes sense, with logs and screenshots.** Anything else will be ignored, permanently.

**The quickest way to report a bug:** in the app, **Settings → About → Report on GitHub**. It opens the bug form with the
version, your system and the end of the log already filled in, and shows the whole log file in Explorer to drag in. Your
Windows user name, PC name and e-mail addresses are taken out of the log first, and nothing is sent until you submit.

Otherwise, open an issue from the [templates](https://github.com/LorenzoKy/Hybrid-Launcher/issues/new/choose). Logs are in
`%LOCALAPPDATA%\HybridLauncher\logs`: one file per session of the app, named after the date and time it started.

For collaborations and business enquiries (not bug reports), write to
[lorenzoillumino@gmail.com](mailto:lorenzoillumino@gmail.com) or to **LorenzoKy** on Discord.

## Support the project

Hybrid Launcher is **free**, and support helps cover its costs. Support is really appreciated: it encourages me to keep
developing Hybrid Launcher. But be warned: for now, updates come at **random times and are not guaranteed**. Thank you.

You can support it on [Ko-fi](https://ko-fi.com/lorenzoky), from the button at the top of this page or the **Sponsor**
button of this repository.

## Third-party software

Hybrid Launcher downloads, runs or links to software and services made by other people. **None of them is affiliated
with me or with this project, and none of them endorses it.** Each belongs to its authors, under its own license and terms;
problems with one of them belong to its own project, not here.

| Software or service | Owner | How the app uses it |
|---|---|---|
| [OptiScaler](https://github.com/optiscaler/OptiScaler), [OptiPatcher](https://github.com/optiscaler/OptiPatcher) | The OptiScaler team | Downloaded from GitHub and installed through OptiScaler's own setup script, unchanged |
| [ReShade](https://reshade.me) | crosire | Official installers downloaded from reshade.me and run unchanged |
| [RenoDX](https://github.com/clshortfuse/renodx) | clshortfuse and the mod authors | Mods downloaded from their releases or picked up from your Downloads folder |
| Luma | Its mod authors | Mods downloaded from their releases |
| [PCGamingWiki](https://www.pcgamingwiki.com), [HowLongToBeat](https://howlongtobeat.com), store pages | Their owners | Game information shown on the game pages |
| [Nexus Mods](https://www.nexusmods.com) | Nexus Mods | Links only; its logo is Nexus Mods' trademark |
| Steam, Epic Games, GOG, Xbox / Game Pass | Valve, Epic Games, CD PROJEKT, Microsoft | Libraries read from this PC; sign-in and downloads through their public services, only when you sign in |
| OneDrive, MEGA | Microsoft, MEGA Limited | Cloud saves, only when you turn them on |
| SteamKit2 (LGPL), MegaApiClient (MIT), Oxanium font (SIL OFL) and other libraries | Their authors | Built into the app under their licenses |

Hybrid Launcher is not affiliated with Valve, Epic Games, GOG, Microsoft, AMD, NVIDIA, Intel or any of the projects
above. All trademarks belong to their owners. Signing in to a store with a third-party app may be against that store's terms
in some cases: check them, the choice is yours.

## Documentation

| Guide | What is in it |
|---|---|
| [Getting started](docs/getting-started.md) | First start, the welcome tour, adding games |
| [User guide](docs/user-guide.md) | Library, game pages, OptiScaler, mods, downloads, My PC, settings |
| [Cloud saves](docs/cloud-saves.md) | OneDrive, MEGA or a folder, one or several; how syncing and conflicts work |
| [Updates](docs/updates.md) | How the app updates itself |
| [Privacy and data](docs/privacy.md) | What is stored, where, and what is sent where |
| [FAQ and troubleshooting](docs/faq.md) | Common questions, logs for bug reports |
| [Get in touch](SUPPORT.md) | E-mail and Discord for collaborations and business enquiries, issues |
| [Contributing](CONTRIBUTING.md) | Joining as a contributor or maintainer |
| [Changelog](CHANGELOG.md) | What changed in each version |

## For developers

```bash
dotnet build src/HybridLauncher/HybridLauncher.csproj -c Release -p:Platform=x64
```

Needs the .NET 10 SDK on Windows. The repository layout, tests and the release process are in
[docs/development.md](docs/development.md).

## License

Hybrid Launcher is free and open source software, released under the **[GNU General Public License v3](LICENSE)**.

- **You can** use it, read the code, build it, change it, contribute to it, fork it and share it.
- **Forks and unofficial builds** you share must stay under the GPL v3, with their source code, and must credit this
  project where their users can see it: *Based on Hybrid Launcher by LorenzoKy*, with a link to this repository. This
  credit is an additional term under section 7(b) of the GPL v3, written in [NOTICE.md](NOTICE.md).

Third-party libraries and mods keep their own licenses.

## Disclaimer

**By downloading or using Hybrid Launcher you accept this disclaimer.**

- Hybrid Launcher is provided **"AS IS"**, without warranty of any kind, express or implied, including but not limited
  to the warranties of merchantability, fitness for a particular purpose and non-infringement.
- **I guarantee nothing**: not that it works, not that it works on your PC or with your games, not that it is free of bugs,
  not that it will be updated, fixed or supported.
- **You use it entirely at your own risk.** You alone are responsible for its use and for what follows from it, including
  but not limited to: damaged or deleted game files, lost or corrupted saves, broken game installs, problems with
  anti-cheat systems, suspended or banned accounts, lost data, hardware or software issues.
- Mods change how games run. Some games and online services do not allow them. **Check the rules of every game and service
  before you mod it**, and never use mods in online or competitive games that forbid them.
- In no event shall the author be liable for any claim, damages or other liability, whether in an action of contract, tort
  or otherwise, arising from, out of or in connection with the software or its use.
- Back up what matters to you (saves, game folders, settings) before using any feature that changes it.
- **This project is built with the use of AI.** That does not mean it takes no time, design or money: planning it, testing
  it and fixing it still take time, and the tools are paid for. It also means the code can contain mistakes, one more reason
  the points above apply.

If you do not agree with all of the above, do not use Hybrid Launcher.

---

<p align="center">Made by <a href="https://github.com/LorenzoKy">LorenzoKy</a>.</p>
