<p align="center">
  <img src="docs/images/logo.png" width="128" alt="Hybrid Launcher logo: a line-drawn pizza slice in white with red pepperoni rings on a dark rounded square" />
</p>

<h1 align="center">Hybrid Launcher</h1>

<p align="center">
  <b>The Hybrid Launcher: manage your games,<br />manage your mods, enhance the experience.</b><br />
  Steam · Epic Games · GOG · Xbox / Game Pass · your own folders
</p>

<p align="center">
  <a href="https://github.com/LorenzoKy/Hybrid-Launcher/releases"><img src="docs/images/button-download.svg" height="52" alt="Download Hybrid Launcher" /></a>
  &nbsp;
  <a href="https://ko-fi.com/lorenzoky"><img src="docs/images/button-support.svg" height="52" alt="Support Hybrid Launcher on Ko-fi" /></a>
</p>

<p align="center">
  <a href="https://github.com/LorenzoKy/Hybrid-Launcher/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/LorenzoKy/Hybrid-Launcher?include_prereleases&label=download&color=D7143A" /></a>
  <img alt="Windows 11, 64-bit" src="https://img.shields.io/badge/Windows-11%20%C2%B7%2064--bit-2A2E38" />
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-555" /></a>
  <a href="https://lorenzoky.github.io/Hybrid-Launcher/"><img alt="Website" src="https://img.shields.io/badge/website-lorenzoky.github.io-555" /></a>
</p>

<div align="center">

**🧭 Jump to:** [🎮 What it is](#what-is-hybrid-launcher) · [📦 Quick start](#quick-start) · [✨ Features](#features) · [❓ FAQ](#faq) · [📚 Docs](#documentation) · [🐞 Report a bug](#reporting-bugs) · [☕ Support](#support-the-project) · [📜 License](#license) · [⚠️ Disclaimer](#disclaimer)

</div>

---

## What is Hybrid Launcher

A free and open source Windows 11 app that **finds the games on your PC**, gives each one **its own page**, and installs the
mods that make them run and look better: **OptiScaler**, **ReShade**, **RenoDX** and **Luma**. A launcher and a mod tool
in one window, for several stores at once.

<p align="center">
  <img src="https://github.com/user-attachments/assets/f3d5303a-d989-4724-8d4f-76170061e6a7" width="100%" alt="A game's page in Hybrid Launcher: the game list on the left, the banner, the launch bar with Play, HDR and Cloud, the tabs and the store details" />
</p>

> [!IMPORTANT]
> **Not a beginner-first app.** It is meant for people with some modding experience: know the mods it handles and how
> they change your game folder, and you will get much more out of it.

## Quick start

1. **Download** `HybridLauncher-<version>-win-x64.zip` from [Releases](https://github.com/LorenzoKy/Hybrid-Launcher/releases/latest).
2. **Extract** it anywhere you can write, for example `C:\Games\Hybrid Launcher`.
3. **Run** `HybridLauncher.exe`, choose the features you want, then pick a game and install a mod from its tabs.

Windows 11 (or an up-to-date Windows 10), 64-bit. Nothing to install: the runtime ships in the `app` folder, settings live
in `%LOCALAPPDATA%\HybridLauncher`, and the app [updates itself](docs/updates.md) after asking once.

## Features

**Your games**
- **One library** for Steam, Epic Games, GOG, Xbox / Game Pass and your own folders: covers, play time, achievements,
  pins, search, filters, renaming, controller support.
- **A page per game** with Play, store details, trailer, reviews, HowLongToBeat, PCGamingWiki fixes, links and notes.
- **Direct downloads** from your Steam, Epic and GOG accounts, with pause, resume and updates that leave your mods in place.
- **Cloud saves** on OneDrive, MEGA or a folder, with history and conflict handling.

**Mods**
- **OptiScaler** through its own installer, with a quick panel, profiles, saved versions, custom builds and
  **custom DLLs** (extra FSR, DLSS or DLSSG dlls per game).
- **ReShade, RenoDX and Luma**, matched to the game where possible; removal keeps your presets.
- **Backups** of every file a mod replaces, so removing it puts the game back.
- **Smart HDR**: Windows HDR on when the game starts, off when it closes.

**Extras**
- **Achievements** with rarity, a platinum trophy and a pop-up you design.
- **Quick access** shortcuts per game, **My PC** with your hardware and driver upscaler versions, tray mode, start with
  Windows, a welcome tour and a one-click bug report.
- **Make it yours**: keep only the features you need (*Everything*, *Only OptiScaler*, *Just a launcher*…), and toggle,
  drag or hide tabs, launch bar items and colour themes.

<p align="center">
  <img src="https://github.com/user-attachments/assets/4847c3da-0a9a-43c3-91ef-3180316ca6e7" width="100%" alt="The first-start picker in Hybrid Launcher: five starting points, a live preview of a game's page, and a switch for each feature" />
</p>

Everything is explained in the [user guide](docs/user-guide.md).

## FAQ

<details>
<summary><b>Do I need to sign in to my stores?</b></summary>

No. Without accounts it reads the games your launchers installed. Signing in only adds direct downloads and your full
owned library.
</details>

<details>
<summary><b>Can I go back to the original game files?</b></summary>

Yes, when the backup is on (the default): removing a mod puts back the files it replaced.
</details>

<details>
<summary><b>Can mods get me banned?</b></summary>

They can, in games and services that forbid them. Never use mods in online or competitive games that do not allow them.
</details>

<details>
<summary><b>Can I build it, change it or fork it?</b></summary>

Yes, it is GPL v3: see the [license](#license) and [docs/development.md](docs/development.md).
</details>

More in [FAQ and troubleshooting](docs/faq.md).

## Documentation

[Getting started](docs/getting-started.md) · [User guide](docs/user-guide.md) · [Cloud saves](docs/cloud-saves.md) ·
[Updates](docs/updates.md) · [Privacy and data](docs/privacy.md) · [FAQ](docs/faq.md) · [Contributing](CONTRIBUTING.md) ·
[Changelog](CHANGELOG.md) · [For developers](docs/development.md)

## Reporting bugs

> [!WARNING]
> **Reports and requests are only considered when they are detailed, understandable and come with logs and
> screenshots.** Anything else is ignored.

The quickest way: **Settings → About → Report on GitHub** in the app. It fills in the version, your system and the end of
the log (with your user name, PC name and e-mails taken out) and nothing is sent until you submit. Otherwise use the
[issue templates](https://github.com/LorenzoKy/Hybrid-Launcher/issues/new/choose); logs are in
`%LOCALAPPDATA%\HybridLauncher\logs`.

Collaborations and business enquiries: [lorenzoillumino@gmail.com](mailto:lorenzoillumino@gmail.com) or **LorenzoKy** on
Discord ([more](SUPPORT.md)).

## Support the project

Hybrid Launcher is **free**. For now it is funded **only out of my own pocket, and those funds are limited**: there is
**no guarantee it will keep going**. Your support could make that possible: [Ko-fi](https://ko-fi.com/lorenzoky) or the
**Sponsor** button of this repository. Contributors and maintainers are welcome too ([CONTRIBUTING.md](CONTRIBUTING.md)).
Thank you.

## Third-party software

Hybrid Launcher downloads, runs or links to software made by other people. **None of them is affiliated with this project
or endorses it**; each belongs to its authors under its own license, and its problems belong to its own project.

<details>
<summary><b>Who makes what</b></summary>

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

</details>

Not affiliated with Valve, Epic Games, GOG, Microsoft, AMD, NVIDIA, Intel or any project above; all trademarks belong to
their owners. Signing in to a store with a third-party app may be against its terms in some cases: the choice is yours.

## License

Free and open source under the **[GNU General Public License v3](LICENSE)**: use it, read it, change it, fork it, share
it. Forks and unofficial builds must stay GPL v3 and show *Based on Hybrid Launcher by LorenzoKy* with a link to this
repository (a section 7(b) term, see [NOTICE.md](NOTICE.md)). Third-party libraries and mods keep their own licenses.

## Disclaimer

**By downloading or using Hybrid Launcher you accept this disclaimer.** It is provided **"AS IS"**, without warranty of
any kind, and **you use it entirely at your own risk**.

<details>
<summary><b>Read the full disclaimer</b></summary>

- Hybrid Launcher is provided "AS IS", without warranty of any kind, express or implied, including but not limited to the
  warranties of merchantability, fitness for a particular purpose and non-infringement.
- **I guarantee nothing**: not that it works, not that it works on your PC or with your games, not that it is free of bugs,
  not that it will be updated, fixed or supported.
- **You alone are responsible** for its use and for what follows from it, including but not limited to: damaged or deleted
  game files, lost or corrupted saves, broken game installs, problems with anti-cheat systems, suspended or banned
  accounts, lost data, hardware or software issues.
- Mods change how games run. Some games and online services do not allow them. **Check the rules of every game and service
  before you mod it**, and never use mods in online or competitive games that forbid them.
- In no event shall the author be liable for any claim, damages or other liability, whether in an action of contract, tort
  or otherwise, arising from, out of or in connection with the software or its use.
- Back up what matters to you (saves, game folders, settings) before using any feature that changes it.
- **This project is built with the use of AI.** That does not mean it takes no time, design or money: planning it, testing
  it and fixing it still take time, and the tools are paid for. It also means the code can contain mistakes, one more reason
  the points above apply.

If you do not agree with all of the above, do not use Hybrid Launcher.

</details>

---

<p align="center">Made by <a href="https://github.com/LorenzoKy">LorenzoKy</a>.</p>
