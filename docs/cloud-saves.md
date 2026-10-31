# Cloud saves

Settings → **Cloud saves**. Switch it on, then pick where the saves go: one place, or several at once.

## Where the saves go

| Option | How it works |
|---|---|
| **OneDrive** | Uses the OneDrive folder that Windows' own OneDrive app keeps in sync. No sign-in in the app: if OneDrive is signed in on this PC, **Use** is one click. If it is not set up, *Set up OneDrive* opens the OneDrive app (or its download page) and *Check again* looks once more. |
| **MEGA** | *Sign in* opens a sign-in page in your own browser, served by the app on your PC (MEGA has no sign-in for apps). Your e-mail and password go to MEGA once; the app keeps only the session MEGA opens, encrypted for your Windows user. Saves are end-to-end encrypted by MEGA. |
| **A folder of your choice** | A NAS, a USB drive, or a folder another sync app keeps. |

**Use** and **Stop using** on each row turn it on and off, so OneDrive and MEGA (and a folder) can be on together: every
upload then goes to each of them, and the saves come back from whichever holds the newest copy. One that was offline or signed
out catches up at the next sync.

Every option uses the same layout: a folder named **Hybrid Cloud Saves** with one folder per game, holding
`save.zip`, `manifest.json` and a `history` folder with the older copies. A folder with an earlier name
(*Hybrid Game Launcher Saves*, *Venom Cloud Saves*) is renamed on first use.

## How syncing works

- **Before a game starts**, saves that are newer in the cloud come to this PC. The saves already here are zipped into the
  app's backups first.
- **After it closes** (however it was started), the saves that changed go up. The cloud's previous copy is kept as an older
  copy (5 per game by default; change it under *Copies and sync*).
- **Changed on both sides** (you played on two PCs without syncing): the app asks which saves stay; the other copy is kept too.
- **What goes up** for each game: its saves, its settings folder when PCGamingWiki names one that is not the saves folder, and
  the achievements and stats of a Steam emulator (Goldberg / GSE) when the game runs with one.
- "Changed" is decided by a hash of the files' contents, so copies and file dates never fool it.
- The saves folder comes from PCGamingWiki; change it from the game's **Cloud** button if the game keeps its saves elsewhere.
  Folders that hold far more than one game's saves (Documents, your user folder, a whole drive) are never synced.
- A cloud that is slow or offline never stops a game: it starts with the saves on this PC, and they go up after the next session.
- Use the same OneDrive or MEGA account (or the same folder) on every PC.

*Sync all now* checks every game at once; games that changed on both sides are left for you to decide from their page.
