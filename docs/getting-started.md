# Getting started

## 1. Install

Download `HybridLauncher-<version>-win-x64.zip` from
[Releases](https://github.com/LorenzoKy/Hybrid-Launcher/releases), extract it to a folder you can write to (for example
`C:\Games\Hybrid Launcher`) and run `HybridLauncher.exe`. There is no installer and nothing else to install.

> Avoid `C:\Program Files`: the app works there, but updating itself then needs administrator rights each time.

## 2. The first start

The app scans Steam, Epic Games, GOG and Xbox / Game Pass and lists every installed game in the side list, grouped by
launcher. First it asks **what you need**: a sheet over the window with ready-made choices (*Everything*, *No
achievements*, *OptiScaler + ReShade*, *Only OptiScaler*, *Just a launcher*), a preview of a game's page with that choice,
and a switch for each feature if you want your own mix. What you leave out is only hidden (no tabs, buttons or pop-ups for
it): nothing is installed or removed, and **Settings → Features → Choose features** changes it any time.

Then the **welcome tour** starts, about the features you kept. Each tip sits beside the real control it explains, and the tour opens the pages
it talks about so you see them for real:

1. **Your games**: the list, the search box and the sort and filter button.
2. **A game's page** (when you have a game installed): Play, Smart HDR, the tools tabs and the game's settings gear.
3. **Library**, **Downloads** and **My PC**.
4. **Adding games** and **Settings**.

*Next* and *Back* move through it; *Skip tour* or Esc ends it, and you go back to where you were. Take it again from
**Settings → Features → Welcome tour**.

## 3. Add what the scan did not find

**Add a game or games folder**, at the bottom of the side list, adds one exe, or a folder where each sub-folder is a game.

## 4. Sign in to your stores (optional)

Open **Downloads → Accounts** and sign in to Steam, Epic or GOG to see every game you own and install them without the store
apps. Signing in is optional: without it, the app still lists and starts your installed games.

## 5. Turn on cloud saves (optional)

**Settings → Cloud saves**: switch it on and pick **OneDrive** (one click, if Windows' OneDrive app is signed in), **MEGA**, or
a folder. See [cloud-saves.md](cloud-saves.md).

## Moving from Venom

Hybrid Launcher was called Venom before version 0.25. The first start of 0.25 moves your old data folder
(`%LOCALAPPDATA%\Venom`) to `%LOCALAPPDATA%\HybridLauncher`, keeps your store and MEGA sign-ins, and renames the cloud
saves folder (now *Hybrid Cloud Saves*). Nothing has to be set up again. Google Drive cloud saves were removed in 0.25:
pick OneDrive, MEGA or a folder instead.
