# Changelog

## 2.0.0 — 2026-11-13 · Dashboard, Mods tab and DLSS 5 (experimental)

**Hybrid Launcher 2.0** is the biggest update so far! DLSS5, Auto tab, reworked Achievements managing and much more!

> ☕ Hybrid Launcher is free, and this version, like the whole project, took a lot of time and money to make. If it is
> useful to you, a tip on [Ko-fi](https://ko-fi.com/lorenzoky) helps keep it going. Thank you!

Everything below is new since 1.3.2:

### ✨ New: the game page

- **Dashboard.** A game's page now opens on the Dashboard (in place of the Game tab): trailer and screenshots, the
  latest update with its notes, news, description, wiki notes, requirements and reviews, plus side cards for your
  activity, achievements, mods, how long to beat, controllers, scores and languages. Every card folds, and the gear
  picks which ones show and their order.
- **News and patch notes inside the app**, in large, readable text with pictures, videos and the build each one shipped
  with. *Open on Steam* is one click away.
- **Version history** of a Steam game, like SteamDB's builds table, with the build installed on your PC.
- **Custom cover** per game: Game settings → Library → Cover.
- **Tab bar in two groups**: the Dashboard and the game's own tabs, then OptiScaler, Mods and the info tabs.

### ✨ New: mods

- **Mods tab** (was ReShade & Mods): a table of every mod in the game folder, with its version, what loads it, whether
  it works and whether Hybrid Launcher put it there, then a section per mod (ReShade, RenoDX, Luma, DLSS 5, DXVK,
  VKD3D-Proton).
- **Mod check.** A warning on the game's banner when something keeps a mod from loading (a game update removed it,
  ReShade loads twice, RenoDX without ReShade's add-on build, DXVK without its dxgi.dll…), with a button that fixes it.
- **The page follows the folder**: a mod added or removed by hand shows at once.
- **DLSS 5 (early, experimental)**, set up step by step: mochizuki0323's **DLSSNR-AMD** for Radeon RX 9000 (RX 7000
  experimental), through OptiScaler or ReShade, and janblade's **F5 Multipass** for NVIDIA (or its vendor-neutral
  DirectX 12 port). NVIDIA's model file is picked once from your own copy, never downloaded or shipped. Young community
  projects: expect games where it does not work yet.
- **Auto tab (experimental).** Reads the game and your PC, proposes the mods that fit, and installs the ones you tick
  together, in the right order and with settings that work together. Generic RenoDX or Luma mods are flagged and never
  ticked for you. Installing each mod from its own tab is still the recommended way.
- **DXVK, DXVK-gplasync and VKD3D-Proton** in a click, with async, HDR and a frame cap as switches; uninstall puts the
  game's files back.
- **OptiScaler's file name** (dxgi.dll, winmm.dll…) changed without reinstalling.
- **Plugins & DLLs** get their own section in the OptiScaler tab, and OptiScaler's settings are described in plain
  words.
- **Old nightly builds** removed from Saved versions in one click.

### ✨ New: library and stores

- **Ubisoft Connect, EA app and Rockstar Games Launcher** games are found, started and uninstalled through their
  launcher.
- **New Library layout**: your whole library in one line, the last played game on a large banner, your latest
  achievements beside it, then the covers. *Auto* size fits every cover in the window.
- **Play and Stop on the covers**, and covers that pop a little under the pointer.
- **Tags on the covers**: play time, mods, upscalers and achievements, or the bare art.
- **Drag and drop** a game's .exe or a games folder onto the window.
- **Recent games in the taskbar** (right-click Hybrid Launcher's taskbar button).

### ✨ New: achievements

- **Xbox and PC Game Pass achievements**, with pop-ups like Steam's while the game runs.
- **Local achievements from every known place** on this PC, plus folders you add. Settings → Achievements → Sources has
  a switch per source (Steam and GOG included).
- **On unlock**: an optional screenshot, a short controller rumble, and progress pop-ups for achievements with steps.
- **Unlock history** with date, game and screenshot, exported as CSV or JSON.
- **Search** in a game's Achievements tab, and **PlayStation pop-up backgrounds** (Original, Light, Dark, Trophy's metal
  or your colour).

### ⚡ Faster and smoother

- **Game pages open at once**: the last ones you opened stay ready, and the next likely ones are prepared while the
  window is idle.
- **Smooth mouse-wheel scrolling** everywhere, and everything the app moves by itself runs in step with your display's
  refresh rate (60, 120, 144 Hz or more).
- **Quicker answers**: no more delay on the first click or scroll after a pause, and achievement pop-ups no longer come
  up late or stutter while a game runs.
- **Ready after the first start**: game art, store data, wikis and news are fetched in the background, so pages open
  from the disk.

### ✨ New: look and feel

- **Interface size** from 50 to 200%, **scroll bars** shown or hidden, and **Shading** (light from above, glows, lifted
  covers): Settings → Appearance.
- **A darker default look** with flat icons.
- **Settings reorganised**: tabs and search in a side column. Search also finds settings with a typo or by other common
  words.
- **Back to the top** button while the tab bar is pinned.
- **Tool tabs centred** (OptiScaler, Mods, Ultra+), with simpler install cards.
- **Game page cache** size and a button to empty it (Settings).
- **Features page** in two columns, with a truer preview.
- **No tooltips over the folded game list.**

### 🐛 Fixes

- **Play button**: every state (*Starting…*, *Download*, *Stop*…) now has Play's size and no longer looks squeezed.
- **Settings' search box** no longer takes the focus by itself.
- **Back from a game**: the window comes back in front and Steam's *last played* date is right straight away.

### 🎨 Changes

- **Nvidia spoofing starts off** for games OptiPatcher supports, until you set it yourself.
- **Setup options easier to read**, with a gold star beside OptiPatcher when it supports the game.
- **The mod warning on the banner names every problem.**
- **OptiScaler development builds** are named by version line and day, e.g. *v10 dev-nightly · 10 Nov*.
- **Settings → About**: the credits are one short note.

## 1.3.2 — 2026-11-06 · Mainly a hotfix for 1.3.1

**Hybrid Launcher 1.3.2** is mainly a hotfix for [1.3.1](#131--2026-11-06--achievement-pop-ups-controller-and-settings-search):
the fixes below, plus fuller credits. Everything new in 1.3.1 is listed in its section. If you have 1.3.0 or 1.3.1, the app
offers this update by itself a few seconds after it starts.

### 🐛 Fixes

- **Settings → Achievements → Sounds:** the *Every achievement* sound's play button played the gold sound when gold had a
  file of its own. It plays its own file now (or Windows' sound when none is set). The sounds of real pop-ups were not
  affected.

### 🎨 Changes

- **Credits:** Settings → About, the README and the site now name every project and service Hybrid Launcher works with:
  the OptiScaler forks and their authors, Luma, Ultra+, PCGamingWiki, HowLongToBeat, Metacritic, SteamDB, Google Drive and
  GitHub, NVIDIA's DLSS files (never shipped), and that the PlayStation, Xbox, Steam, GOG and Epic Games *Like* pop-up looks
  are Hybrid Launcher's own drawings, not affiliated with those companies.

## 1.3.1 — 2026-11-06 · Achievement pop-ups, controller and settings search

**Hybrid Launcher 1.3.1** redraws the achievement pop-up from scratch, with looks like the consoles' and stores', the game's
own platinum and your own sounds; adds a Controller tab with button remapping and a Home button that also opens the trophy you
just earned; lets you search every setting; keeps your play time; and finds Epic games 1.3.0 missed.

### ✨ New

- **Achievement pop-up looks.** Settings → *Achievements* → *Look*: Hybrid Launcher, PlayStation 5 Like, PlayStation 4 Like
  (white or black), Xbox Like, Steam Like, GOG Like, Epic Games Like, Minimal, or *Match the game's store*. The console- and
  store-like looks are Hybrid Launcher's own drawings inspired by them, not the originals: they follow their size, place and
  motion, at the size of the other looks on a desk monitor; where an original has the console's logo, Hybrid Launcher's own
  mark is drawn instead, and the trophy is Hybrid Launcher's own in the bronze, silver, gold or platinum of the tier.
- **Where the original shows it.** The default position puts each look where its console or store shows it (PS5 top right,
  PS4 top left, Xbox bottom centre…). Top centre and Bottom centre can be picked too.
- **Preview with a real game.** Settings → Achievements shows one of a game's real achievements exactly like an unlock would
  look. Nothing is unlocked or written.
- **More sources.** GOG achievements are read while a GOG game runs (signed in), and other tools can show an unlock through
  the app's feed folder (Epic, Xbox or anything else). Local achievements have their own switch in Settings → Achievements →
  *Sources* (on by default).
- **Achievements tab in Settings**, with its own *Restore defaults*.
- **Platinum.** The game's own achievement for unlocking all the others (most games have one, like *Tireless — Unlock all
  achievements*) shows as the platinum: its own name and picture, Hybrid Launcher's platinum trophy in platinum and diamond
  blue, always after the other unlocks. Single achievements are bronze, silver or gold by how rare they are (gold under 10% of
  players). *Platinum* in the real-game preview shows that game's platinum.
- **Search all settings.** The box beside the Settings title finds any setting in every tab, by its name, its description or
  its choices, grouped by tab and section. Pick one to go straight to it; Enter goes to the best match, Ctrl+F jumps to the box.
- **Your play time is kept.** Hybrid Launcher keeps its own record of the time you play (always on), so the hours of a game
  come back when you uninstall it and install it again, move it, add it by hand again or get it from another store.
- **Sounds.** Settings → Achievements, under the pop-ups: Windows' notification sound, your own sounds, or none, and a *Sound
  volume* slider. With *My sounds* bronze, silver, gold and the platinum can each play their own .wav, with one for every
  achievement as the fallback. No sounds come with Hybrid Launcher.
- **Sound presets.** Keep sets of your own sounds and switch between them, or load a folder of .wav files in one go (they
  go to bronze, silver, gold and platinum by their names).
- **Controller tab in Settings.** Controller navigation on or off (for people who move the mouse with a controller through
  another tool), and what each button does: any action can be given to any button (A, B, X, Y, LB, RB, LT, RT, View, Menu, L3,
  R3), swapping with the action that had it. New actions: *Search*, and *Previous / Next section* (My PC, Library, Downloads,
  Settings). LB / RB change tab on game pages and in Settings, and the section on pages without tabs.
- **Home button.** Optional (Settings → Controller): the Xbox / Guide or PS button brings Hybrid Launcher to the front from a
  game or any app, on the page of the game being played, and pressed again goes back to the game. Press or hold; works with
  Xbox controllers, handhelds that show up as one, and PlayStation controllers directly or through DS4Windows, DualSenseX or
  Steam Input. Pressed while an achievement pop-up is on screen (or just after it), it opens that achievement in the game's
  *Achievements* tab, lit for a moment, as a console opens the trophy you just earned.
- **Welcome to the new version.** After an update, the first time the window opens, a short window shows what's new in
  that version, with a link to the full list of changes. A first install gets the welcome tour instead.
- **Settings are easier to get around.** Each tab opens with what it holds and a button for each of its sections, which
  scrolls straight to it (mouse, touch or controller).

### 🎨 Changes

- **Notification list.** Bigger bell that swings when something new arrives. Notifications are grouped by day, can be
  filtered by kind (OptiScaler, Ultra+, Hybrid Launcher), removed one by one, and *Clear* can be undone until the list closes.
  Opening one marks it read.
- **GOG achievements** show on the game's page while signed in to GOG.

### 🐛 Fixes

- **Epic Games: some installed games were not found.** Games whose Epic manifest has an empty `MainGameAppName` (The Outer
  Worlds: Spacer's Choice Edition, Red Dead Redemption 2 and others) were taken for DLC and skipped. Thanks to shoftiel2481
  on Discord for tracking it down.
- **Settings cut off on the right at 1080p** ([#5](https://github.com/LorenzoKy/Hybrid-Launcher/issues/5)). In a maximized
  1080p window some Settings tabs (About the most, Notifications and Storage a little) ran past the right edge: the page's
  column was placed by the width its content wanted, then stretched to its full width from there. It now always takes the
  page's width (up to its maximum), centred. A maximized window left at its old size after a resolution change also fits
  the screen again by itself. Thanks to bovanik on Discord and Kindbad on GitHub for the reports.
- The achievement pop-up has a fixed size: it no longer grows with a long description, which could push it past the edge of
  the screen.
- The achievement's picture shows from the first frame: pictures are fetched when a game starts, and a pop-up opens with
  its picture already loaded instead of downloading it while on screen.
- A game's *Achievements* tab: the date and rarity under each achievement no longer end mid-word on a narrow tile, they go on
  two lines. PCGamingWiki tables keep the rounded ends of their shaded rows.
- The version list of a game's OptiScaler tab showed a new build only after a restart, even after the notification about it.
  It now updates as soon as the new build is found, with no extra requests.
- A test build or a second copy of Hybrid Launcher could take over *Start with Windows*, so Windows started that copy at
  sign-in instead of yours. Only the copy that owns the entry rewrites it now, and the switch shows what Windows will do.

💬 **[Join the Discord](https://discord.com/invite/yjXsQehpaR) for more**: news, previews of what comes next, and help.

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
- **Installed mods on the banner.** The banner's top-left corner lists the mods in the game folder with their version
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
- Leaving a game while OptiScaler was installing, then coming back, showed the build that was there before (a Stable install
  read as Nightly) and offered a second install into the same folder. Installs and removals now keep running when you look
  at something else, show their progress when you come back, and only one runs per game at a time.
- ReShade, RenoDX and Luma downloads were silently cancelled when you opened another page.
- A settings file written by a newer version no longer resets an older one to a first start: what it can't read is left
  out, and a file it can't read at all is kept aside (`settings.unreadable-…`) instead of being written over.
- Zip archives are unpacked directly; before, every zip went through Windows' `tar` after a failed first try.
- The game-running check no longer runs into "access denied" on Windows' protected processes every three seconds.
- Closing the feature picker, or changing which features you use, no longer leaves the typing cursor in the game list's
  search box.
- The first heading of OptiScaler's nightly notes showed as raw `###` text.

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

### 🐞 Fixed

- The game list's search box no longer takes the focus (and looks clicked) after you leave the Downloads page, or any page
  opened from a button rather than from the list.

## 1.0.0 — 2026-10-31 · The first release

**Welcome to Hybrid Launcher!** 🍕

This is the first public version: a free Windows app that puts your games in one place and helps you install the mods that
make them run and look better. It is a launcher and a mod tool in one window, built for people who already know a little
about PC modding and want less juggling between programs and folders.

New here? It takes five minutes: download, extract, run, and the app finds your games by itself.

---

### 🎮 Your games

- **One library** for Steam, Epic Games, GOG, Xbox / Game Pass and any game or folder you add yourself. Covers, play time,
  achievements, pins, search and filters, grouped by store or all together.
- **A page for every game**, with Play, last played and play time, trailers and store details, reviews, HowLongToBeat,
  the fixes PCGamingWiki knows about, your links and notes.
- **Direct downloads.** Sign in to Steam, Epic or GOG (optional) to see everything you own and download it from here, with
  pause, resume and updates that fetch only what changed.
- **Cloud saves** on OneDrive, MEGA, a folder of your choice, or several at once, with a history to go back to.

### ⚡ Mods

- **OptiScaler.** Pick Stable, Nightly or your own custom build; new versions are fetched from GitHub by themselves, and
  every version you install is saved and listed, so you can switch back in one click. A **quick panel** holds the settings
  you change most, and **profiles** let you reuse a setup across games. Installation always goes through OptiScaler's own
  setup script, with your answers filled in.
- **ReShade, RenoDX and Luma**, through their own installers and releases, with the RenoDX or Luma mod for the game
  suggested when one exists.
- **Smart HDR.** Turn it on for a game, and Windows HDR switches on when the game starts and off when it closes.

### ✨ And also

- **My PC**: your graphics card, processor, memory and drives, your displays, and the upscaler and frame-generation
  versions your graphics driver provides.
- **Custom achievements** with a pop-up you design, **quick-access shortcuts** per game, controller support, tray mode,
  start with Windows, English and Italian.
- **It updates itself**: it checks for a new version and asks once before updating.
- **One-click bug reports** from *Settings → About*, with personal details taken out of the log first.

---

### 📜 License

Hybrid Launcher is free and open source under the [GNU GPL v3](LICENSE). Fork it, change it, share it: forks and
unofficial builds just credit this project, as written in [NOTICE.md](NOTICE.md).

**Thank you for trying it.** Questions, ideas and collaborations are welcome.
