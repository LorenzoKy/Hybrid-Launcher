# Changelog

## 1.3.0 — 2026-11-04 · Forks, Ultra+ (experimental) and update notifications

**Hybrid Launcher 1.3.0** installs OptiScaler forks and unofficial builds, manages Ultra+ for Unreal Engine games (experimental), shows at a
glance what each game has installed, tells you when a new version is out, gets out of the way while you play and switches
between games faster. If you already have 1.2, the app offers this update by itself a few seconds after it starts.

### ✨ New

- **Forks/Unofficial Builds.** The OptiScaler tab has a fourth source beside Stable, Nightly and Custom build: community
  forks (OptiScaler Venom, F5 DLSS NR Multipass, DLSS NR Multipass MFG, NR PreSR MFG, FSR Ray Regeneration).
  *Add a fork* takes the GitHub address of any other one. Forks install through their own `setup_windows.bat`, like the
  official builds, and their release notes are in the *Release notes* sub-tab. The fork's GitHub address under the version
  list opens its page.
- **Settings of forks.** OptiScaler.ini is read more broadly: the settings a fork or a custom build adds get switches, sliders
  and lists like the official ones (choices written as *Game or OptiScaler*, ranges as *1..3*, values listed as *0 bilinear,
  1 Catmull-Rom…*, notes shared by a group of keys). *Essentials* lists them after its own cards, one card per section marked
  *this build only*.
- **Ultra+ (experimental).** Unreal Engine games that [Ultra+](https://theultraplace.com) supports get an Ultra+ tab: install a
  version (betas on request), update it or go back, add-ons, and Ultra+'s own settings as switches and sliders. *Update
  automatically* and *Notify me of new versions* cover every game; the app looks for new versions at start and every three
  hours. It is new and tried on few games so far: if something looks wrong, the tab's *Uninstall* puts the folder back as it
  was, and a report helps.
- **What's installed, at a glance.** The banner's top-left corner lists the mods in the game folder with their version
  (OptiScaler and where the build came from, ReShade, Ultra+, RenoDX, Luma); a click opens the mod's tab. Turn it off in
  Settings → Appearance → *Game page*, or for one game in its gear → *Customization*.
- **Update available.** A blue button at the top of the OptiScaler tab when the channel or fork your game uses has a newer
  build: one click installs it, with your settings applied again.
- **Get notified on updates.** A switch under the version list, for each source apart (Stable, Nightly, every fork), off until
  you turn it on: a Windows notification when that one gets a new version. A click on it opens the game on its OptiScaler tab.
- **Out of the way while you play.** When a game you started from Hybrid Launcher is running, the window goes to the
  notification area and comes back by itself when the game closes (Settings → Features → *Step aside while a game runs*).
- **Start with Windows in the notification area.** Started with Windows, only the icon appears; *Open the window when it starts
  with Windows* opens the full window too.
- **DLSS NR model file.** Forks and custom builds that need NVIDIA's `nvngx_dlssnr.dll` (DLSS 5 neural rendering) get it from
  one copy Hybrid Launcher keeps: pick yours once under the version list. *On globally* gives it to every game whose build
  needs it, *On for this game* follows that unless you change it for one game.
- **Notifications.** A button top right, beside Minimize: every update notification is kept in its list (OptiScaler builds you follow,
  Ultra+ versions and the ones it installed by itself, new Hybrid Launcher versions), so one you missed can still be read. A
  dot counts the new ones; a click on one opens where it points.
- **Notification settings.** Settings → *Notifications*: for each source (OptiScaler, Ultra+, Hybrid Launcher itself), whether
  it is kept in the app's list, shows a Windows pop-up, both or neither; how long the list keeps them; no pop-ups while a game
  runs (on by default); hide the button. A new Hybrid Launcher version now gets one Windows pop-up too.
- **Hide the DLSS NR row.** If you don't use DLSS NR, the eye icon on its row hides it in every game (Settings → Appearance →
  *Game page* shows it again). Files already set up keep working.
- **Cozy theme.** Settings → Appearance: warm charcoal, terracotta and cream, with every other colour of the app softened to
  match.
- **Background saver.** When another window is in front, Hybrid Launcher rests: moving clips and muted trailers pause,
  nothing is polled and Windows runs it in efficiency mode. It wakes up the moment you click it; downloads keep full speed
  (Settings → Features → Graphics).

### 🎨 Changes

- **New Play button** and launch bar: softer, more modern shading, and every button with its icon on a tinted tile. HDR, Cloud,
  the folders and achievements sit straight on the bar's glass (no second see-through box); their face shows on hover.
  The folder buttons are shorter: *Game folder*, *Saves folder*, *Configuration folder*.
- **Desktop shortcut** moved to the first page of a game's gear (*General*).
- **Play button** keeps nearly the same width in every state (*Downloading*, *Closing…* are drawn a little smaller instead
  of stretching the launch bar), with its icon and word centred; the launch bar's items sit centred in it.
- **Upscaler tags** merge a maker's upscaler and frame generation in one tag (*DLSS 4 · MFG*), in the same lettering as the
  other tags.
- **Tabs in their old order**, centred; tab and sub-tab names a little brighter. Descriptions everywhere a point larger.
- **Quick access suggests modding tools** found on your PC (Vortex, Mod Organizer 2, r2modman, Cheat Engine, Special K…)
  and the mod folders and logs the game already has (Mods, BepInEx, UE4SS, RED4ext…); the game, saves and settings
  folders stay on the launch bar.
- **My PC, Library and Downloads** have larger two-tone icons; the app icon sits beside the HYBRID wordmark.
- **Faster game switching**: the banner, launch bar and tabs show at once; the rest of the tab follows on the next frame.
- **One colour family.** Section icons, tags, status colours and release-note headings share the same lightness and a calmer
  saturation in every theme, so nothing bright jumps out; release-note headings are a quiet bar instead of a coloured band.
- **OptiScaler header**: the version, then the release it came from as a small badge and the build date (no commit hash).
- **Even faster game switching**: a game's banner is kept in memory once seen, and resting the pointer on a game in the list
  gets its banner ready before you click.
- **Hardware acceleration** also pauses while a game runs (*Pause it while a game runs*): the looping clips give their video
  decoders back and the page stops refreshing until the game closes.

### 🐛 Fixes

- Animated GIFs in a game's description no longer stop when you pause the trailer.
- Picking another fork while the first one's versions were still loading could list (and install) the first fork's builds
  under the second one's name.
- Several new versions found in one check now raise one notification; before, the second replaced the first and its click
  opened the wrong game.
- One failed update check (offline, GitHub busy) no longer stops the background checks until the next start.
- Leaving a game while OptiScaler was installing, then coming back, showed the build that was there before (a Stable install
  read as Nightly) and offered a second install into the same folder. Installs and removals of OptiScaler, ReShade, RenoDX,
  Luma and Ultra+ now keep running when you look at something else, show their progress when you come back, and only one
  runs per game at a time.
- ReShade, RenoDX, Luma and Ultra+ downloads were silently cancelled when you opened another page.
- A settings file written by a newer version no longer resets an older one to a first start: what it can't read is left
  out, and a file it can't read at all is kept aside (`settings.unreadable-…`) instead of being written over.
- Zip releases (most forks) are unpacked directly; before, every zip went through Windows' `tar` after a failed first try.
- The game-running check no longer runs into "access denied" on Windows' protected processes every three seconds.
- Closing the feature picker no longer leaves the typing cursor in the game list's search box.
- Changing which features you use no longer leaves the typing cursor in the search box (WinUI put it there when the selected
  item left the list, and it then stayed for good).
- The *Release notes* section showed the notes of a fork other than the one named on its tab (the tab kept the first fork
  picked, the list followed a later pick). It now follows the fork installed in the game, and changes with an install.
- The first heading of OptiScaler's nightly notes showed as raw `###` text.
- Update checks for forks no longer download each fork's list twice (once per spelling of its name), which used up GitHub's
  hourly limit sooner; the first check runs half a minute after start instead of a minute.
- The Ultra+, fork and notification texts are translated into Italian.

## 1.2.3 — 2026-11-02 · New logo

**Hybrid Launcher 1.2.3** has a new logo: a white game controller shaped like an H, on a red tile. If you already have 1.2, the
app offers this update by itself a few seconds after it starts.

### ✨ New

- **New logo everywhere.** The window, the taskbar, the tray icon, the start page, the tour and `HybridLauncher.exe` itself
  show the new controller H. The small sizes (16 to 48 px) are drawn on their own, with thicker lines, so the icon stays
  sharp in the tray and the taskbar.

## 1.2.2 — 2026-11-02 · Several clouds at once

**Hybrid Launcher 1.2.2** makes cloud saves work well with several clouds at once (OneDrive, MEGA and a folder such as Google
Drive), fixes *Start with Windows* and explains errors better. If you already have 1.2, the app offers this update by itself a
few seconds after it starts.

### ✨ New

- **Remove the custom folder.** Settings → Cloud saves → *A folder of your choice* has a *Remove* button: the folder stops
  being used and is forgotten (the saves already in it stay there).
- **Errors say what to check.** Every red message now has a *Possible causes* line under it (no connection, a file in use, a
  disconnected drive, a full disk…) and a *Troubleshooting* link to the matching section of the new
  [troubleshooting guide](docs/troubleshooting.md).
- **Quieter cloud saves.** No more green message after every upload: the game's Cloud button says *Synced*. A message shows
  only when something went wrong, in red, and says which cloud missed the saves (for example *in OneDrive but not in MEGA
  yet*) and why.

### 🐞 Fixed

- **Start with Windows did nothing** ([#3](https://github.com/LorenzoKy/Hybrid-Launcher/issues/3)). Recent Windows 11 builds
  skip a startup entry that is not marked as allowed in Windows' Startup apps, and Hybrid Launcher never wrote that mark. It
  does now, keeps the entry pointing at the copy you run (after moving the folder or unpacking a new version elsewhere), and
  its toggle shows Off when Windows has it turned off. If it was on in an earlier version, 1.2.2 fixes the entry the first
  time it starts. The icon also comes back if the taskbar was not ready yet when Windows started the app.
- **"Synced" when the saves were not in the cloud, and saves not sent after playing.** For games that keep one saves folder per
  account (Marvel's Spider-Man 2 keeps one per Steam account that ever played on the PC), Hybrid Launcher could watch an old
  account's folder that never changes. It now follows the folder the game writes in, and looks again before each game and
  after it closes (a folder you picked yourself is never changed).
- **A cloud that does not answer no longer stops everything.** When MEGA hung on the network, the cloud menu took seconds to
  open, the cloud button got stuck or read "No saves folder", and Play waited. Now each MEGA request has a time limit, a cloud
  that does not answer is left for the next sync while the others work as usual, uploads go to every cloud at once, and the
  button says "Cloud not reachable" instead. With several clouds, "Synced" now means that every one of them holds the saves.
- **The cloud menu opens at once.** The older copies are read in the background and fill in when the cloud answers.
- **A game could wait 45 seconds at Play and then start without its saves in every cloud.** With OneDrive and MEGA both in
  use, a game whose saves were only in one of them had to send them to the other before starting; big saves (Cyberpunk 2077,
  80 MB) never made it in time, so every Play waited and MEGA never got them. Now Play only packs the saves as they are and
  the game starts at once (Cyberpunk: 2 seconds instead of 45); the saves go up while you play.
- **Quitting right after a game no longer drops its upload.** When saves are still on their way to the cloud, Hybrid
  Launcher closes its window at once and quits when they are there (three minutes at most).
- **OneDrive could miss an upload.** Two uploads a few seconds apart (for example after a short game) could find a file
  OneDrive was still sending, and that copy stayed out of OneDrive until the next sync. Hybrid Launcher now waits a moment
  and tries again (Google Drive and other sync apps too).
- **Google Drive as "a folder of your choice".** Picking the Google Drive drive itself (G:) failed at every upload, since
  Google Drive takes no files at its root: Hybrid Launcher now uses its "My Drive" folder. A folder it cannot write in is
  refused when you pick it, with a message, instead of failing later.

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
