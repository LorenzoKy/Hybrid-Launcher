# Changelog

## 1.1.0-rc.1 — 2026-11-01 · Release candidate

The first test build of 1.1. It is a pre-release: the app's own updates do not offer it to 1.0 users, and once 1.1.0 is
out this build updates to it by itself.

### ✨ New

- **Choose what you need, at the first start.** Before the welcome tour, a sheet over the window asks which features to
  keep: *Everything*, *No achievements*, *OptiScaler + ReShade*, *Only OptiScaler* or *Just a launcher*, each with its icons,
  a live preview of a game's page (launch bar and tabs) and a switch per feature for your own mix. What you leave out is
  only hidden: nothing is installed or removed. The tour then talks only about what you kept.
  → [Getting started](docs/getting-started.md#2-the-first-start)
- **Settings → Features → Choose features** opens the same picker any time.
- *Settings → About* shows the full version, release candidates included.
- **Rename a game.** Right-click it in the list or the library → *Rename…*. The new name shows everywhere in the app (list,
  library, game page, messages) and the search finds both names. The game keeps its store name underneath, so the
  OptiScaler wiki, PCGamingWiki and the store pages still find it. Empty the name to go back.
- **Custom DLLs** (OptiScaler nightly, end of October 2026). Use any FSR or DLSS upscaler dll you want, or a DLSSG
  replacement, next to the ones the game or the driver gives you, without replacing any file. A new card in the OptiScaler
  tab keeps them in one shared folder, grouped in entries: each entry has a name (the one OptiScaler's menu shows) and can
  hold an FFX upscaler for DX12 (`ffx`) and Vulkan (`ffx_vk`), a DLSS runtime (`dlss`) and a DLSSG replacement (`dlssg`).
  Add dlls and choose each one's name and kind, move a dll to another entry, rename or delete it; the dll OptiScaler will
  load is marked. A switch per game copies the entry into OptiScaler's own `custom` folder (`OptiScaler\custom` next to the
  renamed dll); pick it in OptiScaler's upscaler or FG list. Renaming an entry renames it in every game, and OptiScaler.ini
  settings that picked it follow the new name. A folder already laid out for OptiScaler, or an old `Extra dlls\4.0.3`
  folder, can be added as it is. Uninstalling OptiScaler takes the files out too.
- **Two new colour themes: Dark and Venom.** *Dark* is a plain near-black background with a soft silver accent, *Venom* a
  deep purple night. Both are always dark.

### 🧹 Changed

- The colour themes Neon city, Toxic, Crimson, Ocean, Amber and Graphite are gone. If you used one, the app switches to
  Hybrid (Graphite to Dark).
- The launch bar's **HDR** and **Cloud** buttons show a plain coloured icon, like the folder buttons beside them: the sun
  in an amber-to-pink sweep for Smart HDR, the cloud in blue while your saves sync.
- Custom DLLs: a shorter description, and a folder icon on each entry.

### 🐞 Fixed

- Custom DLLs: renaming an entry by changing only upper and lower case now renames it in the game too, so it matches
  OptiScaler.ini exactly.

## 1.0.0 — 2026-10-31 · The first release

**Welcome to Hybrid Launcher!** 🍕

This is the first public version: a free Windows app that puts your games in one place and helps you install the mods that
make them run and look better. It is a launcher and a mod tool in one window, built for people who already know a little
about PC modding and want less juggling between programs and folders.

New here? [Getting started](docs/getting-started.md) takes five minutes: download, extract, run, and the app finds your
games by itself.

---

### 🎮 Your games, in one place

- **One library** for Steam, Epic Games, GOG, Xbox / Game Pass and any game or folder you add yourself. Covers, play time,
  achievements, pins, search and filters, grouped by store or all together.
  → [The library](docs/user-guide.md#the-library)
- **A page for every game**, with Play, last played and play time, trailers and store details, reviews, HowLongToBeat,
  the fixes PCGamingWiki knows about, your links and notes.
  → [A game's page](docs/user-guide.md#a-games-page)
- **Direct downloads.** Sign in to Steam, Epic or GOG (optional) to see everything you own and download it from here, with
  pause, resume and updates that fetch only what changed.
  → [Downloads and accounts](docs/user-guide.md#downloads-and-accounts)
- **Cloud saves** on OneDrive, MEGA, a folder of your choice, or several at once, with a history to go back to.
  → [Cloud saves](docs/cloud-saves.md)

### ⚡ Mods, made easy

- **OptiScaler.** Pick Stable, Nightly or your own custom build; new versions are fetched from GitHub by themselves, and
  every version you install is saved and listed, so you can switch back in one click. A **quick panel** holds the settings
  you change most, and **profiles** let you reuse a setup across games. Installation always goes through OptiScaler's own
  setup script, with your answers filled in.
  → [OptiScaler](docs/user-guide.md#optiscaler)
- **ReShade, RenoDX and Luma**, through their own installers and releases, with the RenoDX or Luma mod for the game
  suggested when one exists.
  → [ReShade, RenoDX and Luma](docs/user-guide.md#reshade-renodx-and-luma)
- **Smart HDR.** Turn it on for a game, and Windows HDR switches on when the game starts and off when it closes.
  → [A game's page](docs/user-guide.md#a-games-page)

### ✨ And also

- **My PC**: your graphics card, processor, memory and drives, your displays, and the upscaler and frame-generation
  versions your graphics driver provides. → [My PC](docs/user-guide.md#my-pc)
- **Custom achievements** with a pop-up you design, **quick-access shortcuts** per game, controller support, tray mode,
  start with Windows, English and Italian. → [Settings](docs/user-guide.md#settings)
- **It updates itself**: it checks for a new version and asks once before updating. → [Updates](docs/updates.md)
- **One-click bug reports** from *Settings → About*, with personal details taken out of the log first.
  → [FAQ and troubleshooting](docs/faq.md)

---

### 📜 License

Hybrid Launcher is free and open source under the [GNU GPL v3](LICENSE). Fork it, change it, share it: forks and
unofficial builds just credit this project, as written in [NOTICE.md](NOTICE.md).

**Thank you for trying it.** Questions, ideas and collaborations are welcome, see [Get in touch](SUPPORT.md).
