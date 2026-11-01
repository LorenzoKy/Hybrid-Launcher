# User guide

- [The library](#the-library)
- [A game's page](#a-games-page)
- [OptiScaler](#optiscaler)
- [ReShade, RenoDX and Luma](#reshade-renodx-and-luma)
- [Downloads and accounts](#downloads-and-accounts)
- [My PC](#my-pc)
- [Settings](#settings)
- [Using a controller](#using-a-controller)
- [Good to know](#good-to-know)

Cloud saves have their own page: [cloud-saves.md](cloud-saves.md).

## The library

- **Side list**: your installed games grouped by launcher (Steam, Epic, GOG, Xbox, your games folders). Click a heading to fold
  it, drag it to change the order. Pinned games sit on top. Right-click a game for Play, pin, rename, open its folder, hide
  or uninstall. A green dot marks a game that is running.
- **Rename a game**: right-click it → *Rename…*. The new name shows everywhere in the app and the search finds both names;
  the store name stays underneath, so the wikis and store pages still find the game. Empty the name to go back.
- **Search** filters the side list as you type (**Ctrl+F** jumps to it); the funnel button beside it sorts and filters.
- **Library** (grid): the same games as covers, with tags for play time, OptiScaler, upscalers (DLSS, FSR, XeSS), frame
  generation and achievements. The trophy turns platinum once every achievement is unlocked. The zoom slider sets the cover
  size.
- **Upscaler tags** show the version the game really ships (for example *DLSS 3.7* or *FSR 4.1*), read from its own files;
  where the game has none, the version PCGamingWiki lists. On the game page, hover a tag to see which file it comes from.
- **Filters**: sort by name, last played, date added, play time, achievements or technology; show only games with OptiScaler,
  some launchers or some upscalers; and show the games you own but have not installed (they then appear under *Not installed*).

## A game's page

The top of the page is the game's art at full brightness with the **launch bar** along its bottom edge:

- **Play** starts the game through its store or its exe. While the game starts, the button shows a stop square; while it runs
  it becomes **Stop** (the app asks first, since unsaved progress is lost). Last played and play time sit beside it.
- **HDR** is Smart HDR for this game. The tile says **Smart** when it is on (Windows HDR turns on when the game starts and off
  when it closes, even after a crash or when the game is ended from Task Manager). When it is off, the tile shows what Windows
  HDR is right now: **On** or **Off**, and follows a change made in Windows within seconds.
- **Cloud**: the game's cloud saves: *Synced*, *Goes up after play*, *Newer in the cloud*, or *Choose a copy* when both sides
  changed. Its menu syncs now, brings the cloud's saves here, restores an older copy, changes the saves folder or leaves the
  game out.
- **Open game folder**, and the saves and configuration folders when PCGamingWiki knows them, with their size.
- **The gear** opens the game's properties: *General* (Smart HDR, launch options, store page), *Installed files* (size,
  folders, the exe to start, uninstall), *Library* (pin, desktop shortcut, hide) and *Customization* (which parts of the
  banner and launch bar show).

A game is known to be running however it was started: from its store, a shortcut, its own exe, even before the app was
opened. Play becomes Stop and play time counts.

Under the banner are the **tabs** (drag to reorder, right-click to hide, the gear at the end to customize):

| Tab | What it is for |
|---|---|
| Game | Trailer and screenshots, store description, requirements, reviews, controller support, languages, HowLongToBeat |
| OptiScaler | Install, update or remove OptiScaler; ASI plugins; saved versions; the Quick Panel; every OptiScaler.ini setting; the OptiScaler wiki entry; release notes |
| ReShade & Mods | ReShade, RenoDX HDR mods and Luma mods for this game |
| PCGamingWiki | The game's wiki page with its contents, sections and infobox; every link opens |
| Achievements | Every achievement, unlocked and locked, with rarity |
| Quick access | Your own shortcuts for this game: folders, tools, config files |
| Notes | Notes for this game, saved as you type |
| Links | Store, community, mods, reviews, guides |

Long tabs have sub-tabs under the tab row; they follow you as you scroll.

## OptiScaler

OptiScaler swaps a game's upscaler (DLSS, FSR, XeSS) and adds frame generation. The app installs it the official way: it runs
OptiScaler's own `setup_windows.bat` and answers its questions with your choices.

1. **Choose a version**: Stable, Nightly, or a custom build you imported (a folder or a .zip / .7z / .rar).
2. **Setup options**: the dll name (dxgi, winmm, version…), your GPU, NVIDIA spoofing, OptiPatcher. The defaults suit most games.
3. **Install**, or *Standard .bat install* to answer the setup window yourself.

Everything it replaces is backed up: **Uninstall** restores the folder exactly. The app never changes a game folder while the
game runs.

- **Quick Panel**: the settings you change most, written straight to OptiScaler.ini. Its profile is either *This game only* or
  *Global* (shared by every game set to Global).
- **OptiScaler settings**: every OptiScaler.ini key as switches, sliders and lists with plain names. Your choices are applied
  again after every reinstall or update; profiles carry a set of settings to other games.
- **ASI plugins**: one shared folder; switch a plugin on for a game and it is copied into its install.
- **Custom DLLs** (OptiScaler nightly): extra FSR, DLSS or DLSSG dlls, kept in one shared folder as **entries**. Each entry
  is a folder whose name is the one OptiScaler's menu shows, and can hold an FFX upscaler for DX12 and Vulkan, a DLSS runtime
  and a DLSSG replacement. Turn an entry on for a game and it is copied into OptiScaler's own `custom` folder; then pick it by
  name in OptiScaler's upscaler or FG list. From each dll's menu you can rename it, change its kind or move it to another
  entry; renaming an entry renames it in every game and in OptiScaler.ini.
- **Release notes**: what each version changed, as on GitHub.

## ReShade, RenoDX and Luma

- **ReShade**: pick a version and edition (standard or with add-on support) and the app downloads the official installer.
  With OptiScaler, *Load ReShade through OptiScaler* makes both work together. **Remove ReShade** takes it out of the game and
  keeps ReShade.ini, presets and shaders. *ReShade settings* has the common options and every ReShade.ini key with a search.
- **RenoDX** (HDR): the mods that fit the game. Snapshot builds install in one click; Nexus Mods files are picked up from your
  Downloads folder after you download them yourself.
- **Luma** (HDR and DLSS for DirectX 11 games): the game's own mod, installed with backups.

## Downloads and accounts

**Downloads** lists every game you own on your launchers: the download in progress on top, the queue and finished installs
under it, then your games as covers. Open **Accounts** (top right) to sign in:

- **Not signed in**: the app reads what the launchers keep on this PC; *Install* opens the launcher's install page.
- **Signed in** (Steam with QR code or password, Epic Games, GOG): the app lists your whole library and downloads, pauses,
  resumes and uninstalls games itself (some games may still need their store's own app to run). Only the sign-in token is
  kept, encrypted for your Windows user.
- **Install** shows where the game goes (the store's folder, another Steam library, any folder), the space it needs and the
  space free. One game downloads at a time; *Start now* moves one to the front of the queue. Pause and resume continue where
  they stopped, even inside a big file.
- **When a download ends**, the game's page turns into the installed game's page by itself (Play, mods, folders).
- **Steam games** the app installs into a Steam library get Steam's own record of them. If Steam is closed, it stays closed and
  Play starts the game from its own exe.
- **Epic and GOG games** the app downloads start from their own exe with the store's launch options, even with those
  launchers closed or not installed.
- **Desktop shortcut**: a game the app downloads gets a shortcut on the desktop once it is installed (it starts the game through
  the app, so Smart HDR, play time and cloud saves work). Turn it off in *Settings → Storage*.
- **Game updates**: games the app downloaded are checked at start and every six hours and update by themselves: only the
  files that changed are downloaded, and files the game does not ship (mods, OptiScaler, ReShade, settings) are never touched.
  Turn it off or check now in *Settings → Storage → Game updates*. Games a store app installed are updated by that app.

Xbox / Game Pass games install from the Xbox app; the app lists them once installed.

**Steam friends**: once Steam is signed in, **Friends** appears at the bottom of the game list, with who is online and in
which game. The app only reads the list; it never changes your online status.

## My PC

- **Hardware**: graphics card (video memory, driver and date), processor (cores and threads), memory modules (type,
  speed, part number), Windows build, motherboard and BIOS, drives.
- **Upscaling and frame generation**: for each graphics card, one column per kind (upscaling, frame generation, ray
  reconstruction, latency). Each technology shows its **exact newest version** (for example FSR Upscaling 4.1.2, FSR Frame
  Generation 4.0.2, FSR Ray Regeneration 1.2.1 on an RX 9000; DLSS 4.5 with its SDK 310.9.16 on an RTX 50; XeSS 3.0.2), and
  whether it runs **Native** on the card or only **Through OptiScaler** (dimmed).
  The versions **correct themselves**: once a day the app reads the vendors' own SDK releases on GitHub (AMD's FSR SDK notes,
  Intel's XeSS SDK, NVIDIA's DLSS SDK) and the correction file in this repository (`data/tech-versions.json`), keeping the
  highest version the vendors publish unless the correction file says otherwise. Offline, it shows the last versions it knew.
- **Displays and Windows settings**: resolution, refresh rate, HDR, brightness and colour profile for each display, and
  Windows' switches for games (GPU scheduling, night light, Game Mode, windowed optimisations).

## Settings

- **Appearance**: colour themes (the *Hybrid* theme uses the logo's crimson; *Dark* and *Venom* are always dark), your own colours, Material colours, where icons
  show, what the game page shows.
- **Features**: **Choose features** (the first-start picker again: keep everything, or only OptiScaler, OptiScaler +
  ReShade, no achievements, just a launcher, or your own mix), close to the notification area, start with Windows, the
  welcome tour, the tabs, and the **achievement
  pop-up** (position, screen, size, colour, background, opacity, time on screen, sound, rarity; *Try it* shows a sample).
  **Game files a mod replaces**: back them up first (the default), just replace them, or ask at each OptiScaler or Luma
  install. Without a backup, uninstalling a mod removes its files but cannot bring back the ones it replaced.
- **Cloud saves**: see [cloud-saves.md](cloud-saves.md).
- **Storage**: install folders, game updates, custom OptiScaler builds, saved versions, cache.
- **About**: version, **Updates** (check now, check at start), credits.
- *Restore defaults* resets only the open tab; your games and accounts are never touched.

## Using a controller

The **D-pad or left stick** moves between controls, **A** presses, **B** goes back, **LB / RB** switch tabs, **LT / RT** and the
**right stick** scroll, **Y** plays the open game, **X** opens the right-click menu, **View** or **Menu** jumps to the game list.
The controller is read only while the app is the window in front, so a running game never steers it.

## Good to know

- Your data: `%LOCALAPPDATA%\HybridLauncher` (settings, cache, backups, logs). Deleting it resets the app.
- Closing the window keeps the app in the notification area; *Quit* there closes it for real.
- Desktop shortcuts made by the app start the game through it, so Smart HDR, play time and cloud saves still work.
