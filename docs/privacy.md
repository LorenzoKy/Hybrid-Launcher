# Privacy and data

## What is stored, and where

Everything the app writes outside the game folders you change lives in `%LOCALAPPDATA%\HybridLauncher`:

| Path | What |
|---|---|
| `settings.json` | Your settings, per-game choices, play time |
| `accounts\` | Sign-in tokens for Steam, Epic, GOG and MEGA, encrypted with Windows DPAPI for your Windows user only. Passwords are never stored. |
| `backups\` | Files replaced by mods, and zips of your saves before the cloud replaced them |
| `cache\` | Images, store and wiki pages, downloads in progress, updates |
| `logs\` | One log per session of the app (the newest 20, none older than 30 days), for bug reports |

Deleting the folder resets the app. Sign out first if you want the stores to forget the session too.

## What is sent where

- **Stores** (Steam, Epic, GOG): only when you sign in, to list, download and update your games.
- **Public game information**: store pages, PCGamingWiki, HowLongToBeat, Metacritic, the OptiScaler wiki, to fill the game
  pages. These requests carry the game's name or store id, nothing about you.
- **GitHub**: OptiScaler and mod releases, the app's own update check, and once a day the AMD, Intel and NVIDIA SDK release
  lists that keep My PC's technology versions current.
- **Cloud saves**: only to the place you chose (your OneDrive folder, your MEGA account, your folder).

There is no analytics, telemetry or account of the app's own.
