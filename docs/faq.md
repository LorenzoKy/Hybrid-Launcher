# FAQ and troubleshooting

**A game is missing from the list.**
Check that its launcher is ticked in *Settings → Features* (the stores to scan), or add it with **Add a game or games folder**.

**OptiScaler does not show up in the game.**
Try another dll name in the setup options (dxgi, winmm, version…); some games load only one of them. The game's OptiScaler wiki
entry, in the OptiScaler tab, often says which one works.

**Smart HDR does nothing.**
Your display must support HDR; the HDR tile is disabled when no display can show it. The tile shows *On* or *Off* for the
current Windows HDR state while Smart HDR is off for that game.

**The OneDrive option says OneDrive is not set up.**
Sign in to Windows' OneDrive app (it ships with Windows), then press *Check again*.

**The download finished but the page still says Download.**
Fixed in 0.25: the page now turns into the installed game's page once the library refreshes. If it still happens, report it
with the log.

**Where are the logs?**
`%LOCALAPPDATA%\HybridLauncher\logs`, one file per session of the app (named after the date and time it started; the
newest 20 are kept). **Settings → About → Report on GitHub** opens a bug report with the log filled in.

**How do I reset the app?**
Quit it from the notification area and delete `%LOCALAPPDATA%\HybridLauncher`. Game folders are not affected; remove mods
from the game pages first if you want them gone.

**Is this affiliated with Steam, Epic, GOG, Microsoft, AMD, NVIDIA or Intel?**
No.
