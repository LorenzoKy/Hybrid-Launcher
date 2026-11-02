# Changelog

## 1.2.1 — 2026-11-02 · Smart HDR fix

**Hybrid Launcher 1.2.1** keeps Smart HDR on for the whole game, also for games that start through a launcher. If you already
have 1.2, the app offers this update by itself a few seconds after it starts.

### 🐞 Fixed

- **Smart HDR turned off while some games were still running.** Games that start through a launcher exe and then run another
  exe in a sub-folder, such as Gears of War: E-Day and other Xbox Game Pass games, or many Unreal games, were seen as closed
  when the launcher exited: HDR went off and Stop could not close them. Hybrid Launcher now follows the game's whole folder,
  so HDR stays on until the game itself closes.
- **Smart HDR now works however the game is started.** A game with Smart HDR started from the Xbox app, Steam or a shortcut
  turns HDR on too, not only one started with Play.

## 1.2.0 — 2026-11-01 · Glass and fixes

**Hybrid Launcher 1.2** gives the game page a new launch bar, shows an Update button when a new version is out, and makes OptiScaler install on every Windows, whatever its
language and regional format. If you already have 1.1, the app offers this update by itself a few seconds after it starts.

### ✨ New

- **A new launch bar.** On a game's page, the bar with Play, the play time and the buttons is now a floating pane of frosted
  glass with rounded corners, a little inside the banner. Its light blur keeps the shapes and colours of the game's art,
  and a neutral dark tint keeps the text readable on any picture. The fade at the bottom of the banner is softer, so the
  art shows more.
- **An Update button in the title bar.** When a newer version of Hybrid Launcher is out, a small blue *Update* button
  appears beside Minimize, and stays there until you install it, even if you chose *Later*. One click opens the update.

### 🐞 Fixed

- **OptiScaler would not install with some Windows regional formats.** With a format Windows has only in Unicode (Hindi,
  Bengali, Tamil, Punjabi, Georgian, Armenian, Amharic and others), every install (stable, nightly or a custom build)
  stopped at once with *"1 is not a supported code page"*. The setup script's output is now read in the code page Windows
  really uses for it, and in UTF-8 if that one is unknown.
- **Every other feature is kept out of the same kind of problem.** Whatever the regional format in Windows (another calendar
  such as Thai or Persian, other digits, separators changed in Control Panel), the app shows dates and numbers in US
  English, British English or Italian, the formats it is tested in.

## 1.1.0 — 2026-11-01 · Make it yours

**Hybrid Launcher 1.1** lets you keep only the features you need, adds custom upscaler dlls for OptiScaler, game renaming
and two new dark themes. If you already have 1.0, the app offers this update by itself a few seconds after it starts.

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
  → [User guide, OptiScaler](docs/user-guide.md#optiscaler)
- **Two new colour themes: Dark and Venom.** *Dark* is a plain near-black background with a soft silver accent, *Venom* a
  deep purple night. Both are always dark.

### 🧹 Changed

- The colour themes Neon city, Toxic, Crimson, Ocean, Amber and Graphite are gone. If you used one, the app switches to
  Hybrid (Graphite to Dark).
- The launch bar's **HDR** and **Cloud** buttons show a plain coloured icon, like the folder buttons beside them: the sun
  in an amber-to-pink sweep for Smart HDR, the cloud in blue while your saves sync.

### 🐞 Fixed

- The game list's search box no longer takes the focus (and looks clicked) after you leave the Downloads page, or any page
  opened from a button rather than from the list.

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
