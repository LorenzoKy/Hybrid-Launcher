# Changelog

## 2.0.0 — 2026-11-13 · Dashboard, Mods tab and DLSS 5 (experimental)

**Hybrid Launcher 2.0** is the biggest update so far, and everything below is new since 1.3.2. A game's page now opens on a
**Dashboard** with its news and patch notes read in the app; the **Mods tab** shows every mod in the game folder, what loads
it and what keeps it from working; **DLSS 5** neural rendering can be set up step by step on Radeon and NVIDIA cards (early
and experimental); **DXVK, DXVK-gplasync and VKD3D-Proton** install in a click; Ubisoft Connect, EA app and Rockstar games are
found; an **Auto tab** (experimental) sets up several mods together in one click; Xbox achievements, an unlock history,
game pages that open at once and smooth scrolling come with a long list of reworks. OptiScaler is still the main focus. If you have 1.3.0 or later, the app offers this update by itself a few seconds after it starts.

### ✨ New: the game page

- **Dashboard.** A game's page now opens on the Dashboard, which takes the place of the Game tab. The main column holds the
  trailer and screenshots, the Spotlight (the post Steam's library features for the game, or its newest major update,
  with an outline of its notes), the game's news and updates, the
  description, the wikis' notes, system requirements and Steam reviews. The news list opens on its three newest posts, the
  Spotlight's included, and unfolds five at a time. The side cards show your activity (sessions,
  unlocks, official posts), achievements (the latest unlock and your rarest, drawn large), the mods in the game's folder,
  how long it takes to beat, controllers, details, Steam's review score and Metacritic, and languages. Each part folds;
  the gear on the tab bar picks which parts show and their order. On a narrow window the side cards move under the main
  column.
- **News and patch notes inside the app.** Clicking a post opens it in Hybrid Launcher instead of the browser, in large,
  well-spaced text: its sections as buttons that jump to them, every change as a list (nested lists stay indented),
  pictures and videos, the build it shipped with and whether your copy has it. *Open on Steam* is one click away.
- **Version history** of a Steam game, laid out like SteamDB's builds table: updates per year, a bar per month, and the
  build installed on your PC.
- **Custom cover** per game: Game settings → Library → Cover, any picture (2:3 fits best).
- **Tab bar in two groups.** The Dashboard is a house icon at the head of the game's own tabs (Auto, Quick access, Notes);
  the OptiScaler, Mods and info tabs follow after a thin line. Tabs can still be dragged into your own order, inside their
  group.

### ✨ New: mods

- **Mods tab.** ReShade & Mods is now the Mods tab. It opens on a table of every mod in the game folder: its version, what
  loads it (Windows in place of its own dll, OptiScaler, ReShade), whether Hybrid Launcher put it there and whether it
  works. Each line opens its section or uninstalls the mod, and a click on it lists its files. Below come the sections of
  each mod: ReShade, RenoDX, Luma, DLSS 5, DXVK and VKD3D-Proton.
- **What is installed as what.** Each mod lists its files beside the game exe: which dll is which mod (dxgi.dll is
  OptiScaler, d3d11.dll is DXVK…) with its version, what loads each one (OptiScaler for ReShade64.dll and its plugins,
  ReShade for RenoDX and Luma add-ons), whether it actually loads, and whether Hybrid Launcher put it there. The upscaler
  libraries the game ships, with their real versions, are listed under the table.
- **Mod check.** The game's banner shows a warning when something keeps a mod from loading: OptiScaler, RenoDX, Luma or
  DXVK files a game update or a file check removed or replaced, OptiScaler in the folder twice, ReShade loaded twice (as
  its own dll and through OptiScaler), OptiScaler set to load a ReShade that is not there, RenoDX or Luma beside the normal
  ReShade build (which skips add-ons) or with no ReShade at all, and DXVK without its own dxgi.dll. Each problem has a
  button that fixes it or opens the right section; *Ignore* hides one until it changes.
- **The page follows the folder.** Put a mod in or take one out by hand and the banner, the Mods table and the Dashboard
  update by themselves.
- **DLSS 5 (early, experimental).** A section of the Mods tab sets up DLSS 5 neural rendering, with the steps in order and
  a button for each: what your graphics card and driver can run and which way is recommended, NVIDIA's model file (picked
  once from your own copy, never downloaded or shipped), and the two community mods that run it:
  - **mochizuki0323's DLSSNR-AMD** for Radeon RX 9000 (RX 7000 experimental). Hybrid Launcher downloads the package, runs
    its own installer once to make the networks' weights from your model file, and puts it in a game either as a plugin of
    OptiScaler or, in any 64-bit game without OptiScaler, through ReShade (DirectX 9 to 12 and Vulkan). Network, working
    scale, HUD fix and its other settings are on the page; the game's own files are kept aside and put back on take-out.
  - **janblade's F5 Multipass**, a fork of OptiScaler: NVIDIA NGX on RTX cards with the optional F5Low frame generation, or
    its vendor-neutral DirectX 12 port on other cards.

  A Logs page opens what each one writes when DLSS 5 does not start. These are young community projects and this support
  is experimental: expect games where it does not work yet. Requests and contributions are welcome (see
  [Reporting bugs](https://github.com/LorenzoKy/Hybrid-Launcher#reporting-bugs)).
- **Auto tab (experimental).** It reads the game (64 or 32-bit, graphics API, your graphics card, HDR) and proposes the mods
  that fit it: OptiScaler, DLSS 5, ReShade, RenoDX or Luma, Ultra+, DXVK or VKD3D-Proton. Tick what you want, pick each
  mod's build in its card (OptiScaler's source and version, DLSS 5's method, ReShade standard or add-on), and *Install* puts
  them in the right order with settings that work together: ReShade as ReShade64.dll loaded by OptiScaler, ReShade's add-on
  build when RenoDX needs it, RenoDX or Luma but not both. *Add a build* imports an OptiScaler build from a folder, an archive
  or a fork's GitHub address, as on the OptiScaler tab. A generic RenoDX or Luma mod (one made for every game of an engine,
  not for this one) is marked *Generic* and is never ticked for you. Mods already in the game can be ticked to put another
  build in their place or to uninstall them, and the plan follows the game folder as it changes. The tab is new and can pick
  wrong for some games: installing each mod from its own tab is still the recommended way, and the RenoDX and Luma matches
  are worth checking in their own sections first.
- **DXVK, DXVK-gplasync and VKD3D-Proton.** Two sections of the Mods tab put these Vulkan translation layers in a game:
  DXVK (or DXVK-gplasync) for Direct3D 8 to 11, VKD3D-Proton for Direct3D 12. Pick the release, and the right dlls for the
  game's Direct3D version and bitness go next to its exe. Async, HDR and a frame cap are switches that write dxvk.conf.
  Files of OptiScaler or ReShade are never replaced, and uninstall puts back every file the layer replaced.
- **OptiScaler's file name changed without reinstalling.** Click the file name under the installed OptiScaler (dxgi.dll,
  winmm.dll, version.dll…), or pick another one in Setup options: the file is renamed on the spot, a game file it had taken
  the place of goes back, and any other OptiScaler copy under another name is taken out.
- **Plugins & DLLs** have their own section in the OptiScaler tab, right under Install: ASI plugins and custom upscaler or
  frame generation DLLs.
- **OptiScaler settings in plain words.** Each setting says what it does in a sentence (the ini comment stays in the
  tooltip), choices read like *Auto · FSR 2.2*, and the ini keys show only in *All sections* and in search.
- **Old nightly builds** can be removed from Saved versions in one click: the newest three and any build a game uses stay.

### ✨ New: library and stores

- **Ubisoft Connect, EA app and Rockstar Games Launcher.** Their games are found and listed under their own group, started
  through their launcher (Ubisoft and Rockstar) or their own exe (EA), and uninstalled through the launcher's own entry.
  Settings → Game detection has a switch for each. Copies bought on Steam or Epic stay with Steam or Epic.
- **A new Library layout.** On top, the whole library in one line (games, hours played, achievements); then the game you
  played last on its own banner with its logo, when you played, hours and achievements (click anywhere on it to open the
  game, or Play), and beside it the **latest achievements** you unlocked in any game, each opening its game on that
  achievement. The covers follow under their own bar with the size slider and the filters. Last played first is the new
  default order. *Auto* size makes the covers as big as they can be with every one of them in the window at once (big
  libraries keep the usual size).
- **Play and Stop on the covers.** A Play button comes up in the middle of the cover under the pointer (Stop while the game
  runs), drawn as a raised button that lights up under the pointer and sized to the cover, and the whole cover grows a little
  over its neighbours instead of zooming inside its frame.
- **Tags on the covers**: play time, the mods installed (Opti, RS, RDX, U+), upscalers and achievements on each cover, or
  the bare cover art (Settings or the Library's Filters).
- **Drag and drop** a game's .exe onto the window to add it as a game, or a folder to add it as a games folder.
- **Recent games in the taskbar.** Right-click Hybrid Launcher's taskbar button to start one of your last games.

### ✨ New: achievements

- **Xbox and PC Game Pass achievements**, read from the Xbox app on this PC with no sign-in, or live with your Microsoft
  account (signed in on Microsoft's own page): while an Xbox game runs its unlocks pop up like Steam's.
- **Local achievements from every known place** on this PC, in the formats games keep them in, plus any folder you add.
  Settings → Achievements → Sources shows every source as a tile with its own switch (Steam and GOG can now be turned off
  too) and lists every folder read.
- **On unlock:** a screenshot of the screen the game is on (with or without the pop-up), a short double rumble on the
  controller, and progress pop-ups for achievements with steps (kill 100 enemies…).
- **Unlock history:** every unlock Hybrid Launcher shows, with its date, game and screenshot, exported as CSV or JSON.
- **Search** in a game's Achievements tab.
- **PlayStation pop-up backgrounds.** Settings → Achievements → *PlayStation background*: *Original* (the default), *Light*,
  *Dark*, *Trophy's metal* or *Colour picked above*.

### ✨ New: look and feel

- **Game pages open at once.** The pages of the last eight games you opened stay ready, as you left them: going back to one
  shows it in the next frame instead of building it again. While you are not using the window, the games next to the open one
  in the list and the ones you played last get their pages ready too, Mods tab included, so most games open at once even
  the first time. The library no longer rebuilds its covers each time you come back to it, and the OptiScaler and Mods
  tabs show with the click, their sections following one per frame. A minute after the window goes to the notification area, that
  memory is given back.
- **Smooth mouse-wheel scrolling.** The game page, the library, Downloads, My PC, Settings and the game list glide on the
  mouse wheel instead of jumping: notches turned one after another blend into one movement, and the movement runs in
  Windows' compositor, so it does not stop while a page is still loading. The distance per notch follows Windows' *lines to
  scroll* setting; a touchpad, the scroll bar and the keyboard scroll as before.
- **Vsync, always on.** Everything Hybrid Launcher moves by itself (the wheel scrolling, the controller's right stick, the
  achievement pop-ups) moves once per refresh of your display, equally smooth at 60, 120, 144 Hz or more.
- **Interface size** from 50 to 200% on top of Windows' display scale (below 100% is experimental), and **scroll bars**
  shown (thinner in a small window) or hidden: Settings → Appearance.
- **Shading** (Settings → Appearance): cards lit from above, a glow under the open tab, a sheen on the launch bar and
  covers that lift. Off gives flat surfaces.
- **A darker default look** with a painted window background and flat icons.
- **Settings reorganised.** A side column at the window's left edge holds the title, the search through every tab and the
  tabs as a list, with the open tab's sections under it; the open tab has its own heading and is centred in the window.
- **Better settings search.** It also finds a setting with a typo, by common other words (*dark* finds the colour theme,
  *gamepad* the controller settings) and, in Italian, by its English name. The words you typed are highlighted in the
  results; the down arrow moves into them and Escape clears the search.
- **Back to the top.** While a game's tab bar is pinned to the top of the page, a button with an arrow on its left goes back
  up to the banner.
- **Tool tabs centred.** The OptiScaler, Mods and Ultra+ tabs are centred on most of the window's width and grow with it, up
  to a limit on very wide screens, instead of a row's name at one edge and its switch at the other. OptiScaler's install card is one block instead of
  three numbered steps, and DLSS 5 shows what this game has first, then one card with each part's state and a single button
  that does what is left.
- **No tooltips over the folded game list.** With the list folded to its icons, resting on one no longer covers the page.
- **Features page** (first start and Settings → Features) in two columns, with a truer preview of a game's page.
- **Game page cache** (Settings): how much disk space covers, screenshots and store data may take, and a button to empty it.
- **Ready after the first start.** The first time Hybrid Launcher starts, it fetches what every game's page shows (art,
  store data, wikis, achievements, news) in the background, so pages open from the disk afterwards. Opening the Mods tab
  draws its sections one after the other instead of all at once.

### 🐛 Fixes

- **Achievement pop-ups late or halting while a game runs.** With a game in front Hybrid Launcher runs in Windows'
  efficiency mode, and the pop-up came up to a few seconds late with its first frames stepping. It now leaves efficiency
  mode as soon as an unlock is seen, keeps its frames at the display's rate and reads the picture and rarity ahead of time.
- **Slow answer after a pause.** After a moment with nothing moving, the first turn of the wheel or a click on a game took
  up to 0.2 seconds or more to show. While the mouse is over the window, it now answers at the next frame, and a game's
  page shows in about 60 to 130 ms.
- **Play button:** *Starting…*, *Download*, *Downloading*, *Stop* and *Closing…* are now set exactly like *Play*; longer
  words were drawn smaller and looked squeezed. The button is wider and less tall-looking, with the icon and a larger word
  together in its middle, and its corners are clean.
- **Settings' search box took the focus** whenever a tab or a row was redrawn, and looked clicked. It now takes it only from
  you (a click, Tab, the controller, Ctrl+F).
- **Back from a game.** The game's exit is seen within a second, the window comes back in front (unless you are using
  another app), and Steam games get the right *last played* date straight away.

### 🎨 Changes

- **Nvidia spoofing starts off for games OptiPatcher supports** (OptiScaler tab and Auto tab), until you set it yourself:
  OptiPatcher already opens the upscaler options in those games.
- **Setup options easier to read.** The OptiScaler tab's Setup options show their title and summary larger and brighter,
  and a gold star beside OptiPatcher when it supports the game, which is why spoofing starts off there.
- **A mod warning on the banner names every problem.** With more than one, its tooltip lists them all; a click still opens
  the Mods tab's list with a fix for each.
- **OptiScaler development builds** are named by their version line and build day, for example *v10 dev-nightly · 10 Nov*,
  instead of the placeholder number they carry (10.0.0-dev). A build dropped over an install by hand is shown with its own
  version.
- **Settings → About:** the credits are one short note, the same as the README's.

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
