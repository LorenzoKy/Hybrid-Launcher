# Troubleshooting

Every error in Hybrid Launcher shows up in red at the bottom of the window. Under the message there is a
**Possible causes** line and a **Troubleshooting** link that opens the matching section of this page. Start there; if the
problem stays, [report it](#reporting-a-problem) with the log.

Jump to: [Cloud saves](#cloud-saves) · [Start with Windows](#start-with-windows) ·
[A game does not start](#a-game-does-not-start) · [Mods, OptiScaler and ReShade](#mods-optiscaler-and-reshade) ·
[Downloads, sign-in and updates](#downloads-sign-in-and-updates) · [HDR and display](#hdr-and-display) ·
[Common Windows errors](#common-windows-errors) · [Reporting a problem](#reporting-a-problem)

## Cloud saves

The game's **Cloud** button says where the saves stand: *Synced*, *Syncing…*, *Changed here* and so on. When a sync works
nothing pops up; a message appears only when something went wrong.

| You see | What it means and what to do |
|---|---|
| *The saves of … are in OneDrive but not in MEGA yet* | One of your clouds did not get this copy (it did not answer, or the upload failed). The saves are safe in the others and on this PC; the missing cloud gets them at the next sync. If it keeps happening, look at the cause on the second line: usually the connection, or MEGA's sign-in. |
| *… did not answer in time* | That cloud is slow or offline. The game still starts with the saves on this PC. Check your connection, or MEGA's status if only MEGA fails. |
| *MEGA ended Hybrid Launcher's sign-in* | Sign in to MEGA again in **Settings → Cloud saves**. Changing your MEGA password or signing out of every session also ends it. |
| *The OneDrive option says OneDrive is not set up* | Sign in to Windows' OneDrive app, then press **Check again**. |
| *Access denied* or *the file is in use* on OneDrive | The OneDrive app is still syncing that file. Let it finish (its icon by the clock stops spinning) and press **Sync now**. Files set to *online-only* are fetched first, which takes a moment. |
| *The folder is not there* (a custom folder or Google Drive) | The drive is not connected or the Google Drive app is closed (`G:` disappears with it). Open the app, or pick another folder; **Remove** in Settings → Cloud saves takes the custom folder off. |
| *The saves folder is larger than …* | The folder picked for the game holds more than its saves. Pick the real saves folder from the game's **Cloud** button. |
| *Hybrid Launcher did not find the saves folder* | PCGamingWiki does not list it, or the game keeps it somewhere else. Pick it from the game's **Cloud** button → *Change the saves folder*. |
| The button never says *Synced* | One of the clouds does not have the newest copy yet (see the first row). **Sync now** sends it. |


## Start with Windows

Hybrid Launcher starts **in the notification area**, without opening its window: look for its icon by the clock, or under the
arrow with the hidden icons. To check it really started, open `%LOCALAPPDATA%\HybridLauncher\logs`: the newest log from the
time you signed in says *Started with Windows*.

If it does not start:

1. Open **Settings → Apps → Startup** in Windows (or the *Startup apps* tab of Task Manager) and check that
   **Hybrid Launcher** is on. When Windows has it off, Hybrid Launcher's own toggle shows Off too: turn it on again there.
2. Moved the Hybrid Launcher folder, or unpacked a new version somewhere else? Open that copy once: it points Windows at
   itself. Versions before 1.2.2 did not, and also did not mark themselves as allowed in Windows' Startup apps, which some
   Windows 11 builds need: 1.2.2 fixes both the first time it starts.
3. Keep the folder somewhere that is there when you sign in (not on a drive that connects later).

## A game does not start

- **Is it still running?** Hybrid Launcher waits for a game to close before it starts it again. Stop it from its page, or close
  it in Task Manager.
- **No game exe**: the game's folder moved or the game was uninstalled, or Hybrid Launcher picked the wrong exe. Set it
  with the gear beside Play → *Change game exe*.
- **The launcher opens instead**: Steam, Epic, EA and Ubisoft games often go through their launcher. Hybrid Launcher follows
  the game once it is up; the launcher must be signed in.
- **It closes at once after a mod went in**: remove the mod from the game's page (the original files come back when the
  backup is on), then try another dll name or version.

## Mods, OptiScaler and ReShade

- **OptiScaler does not show up in the game**: try another dll name in the setup options (dxgi, winmm, version…); some games
  load only one of them. The game's OptiScaler wiki entry, in the OptiScaler tab, often says which one works.
- **Install failed / Could not uninstall**: close the game and its launcher first; they keep the dlls in use. A game under
  `Program Files` may need administrator rights: Windows asks.
- **An antivirus removed a file**: some flag dll injectors such as ReShade and OptiScaler. Allow the game folder in your
  antivirus, then install again.
- **ReShade shows nothing**: press Home in the game; check the preset and that the game uses the API ReShade was set up for.

## Downloads, sign-in and updates

- **Could not sign in**: the store's page must load in the sign-in window; a VPN or a blocked domain stops it. Try again
  without them.
- **A download stops or is slow**: Hybrid Launcher resumes it when you press it again; check the free space on the drive.
- **Could not check for updates**: GitHub allows 60 checks an hour from one connection; wait and try again, or download the
  newest zip from the [releases](https://github.com/LorenzoKy/Hybrid-Launcher/releases).

## HDR and display

- **Smart HDR does nothing**: your display must support HDR; the HDR tile is off when no display can show it.
- **Could not change HDR / the display mode**: the graphics driver refused it. Update it, and check the same change works in
  Windows' display settings.

## Common Windows errors

The *Possible causes* line under an error comes from what Windows answered. The usual ones:

| Windows says | Most likely |
|---|---|
| *Access to the path … is denied* | The file is in use or protected: close the game and its launcher; check *Controlled folder access* in Windows Security and your antivirus. |
| *The process cannot access the file because it is being used by another process* | The game, its launcher or a cloud app holds the file. Close them and try again. |
| *Could not find a part of the path* | A drive disconnected, a cloud app closed, or the game moved. |
| *There is not enough space on the disk* | Free some space on that drive. |
| *The operation has timed out*, *No such host is known* | No internet, or the service is down. Try again in a few minutes. |
| *The requested operation requires elevation* | The folder needs administrator rights. Move it to a folder of yours. |

## Reporting a problem

Use **Settings → About → Report on GitHub**: it fills in the version, your system and the end of the log (user name, PC name
and e-mails taken out). Logs are in `%LOCALAPPDATA%\HybridLauncher\logs`, one per session of the app. Say what you did, what
you expected and what happened, and add a screenshot.
