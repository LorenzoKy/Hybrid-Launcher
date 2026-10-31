# Updates

The app updates itself from this repository's [GitHub Releases](https://github.com/LorenzoKy/Hybrid-Launcher/releases).
The only thing you do is confirm.

## What happens

1. A few seconds after the app starts it asks GitHub for the latest release (a single small request; turn it off in
   **Settings → About → Updates → Check at start**).
2. If the release is newer than the running version, a dialog shows the new version and its notes. **Update now** goes on;
   **Later** skips that version at start (the About tab still offers it).
3. The app downloads the release's zip for your processor (x64 or ARM64), unpacks it in its cache, starts the new version
   from there and closes.
4. The new version waits for the old one to close, copies its files over the app's folder (only the files the release ships:
   nothing else in the folder is touched) and opens the updated app, which says *Updated to …* and cleans up the cache.

Your games, settings, accounts and cloud saves live in `%LOCALAPPDATA%\HybridLauncher`, not in the app's folder, so an
update never touches them.

If the app sits in a folder Windows protects (such as `C:\Program Files`), Windows asks for administrator rights for the copy.
If the copy fails, the old version starts again unchanged, and the reason is in the log.

## Checking by hand

**Settings → About → Updates → Check for updates.** When a newer version is found, the button becomes *Update to x.y.z*.

## Notes

- Drafts and pre-releases are never offered.
- While the repository is private, GitHub does not show its releases to the app, so the check finds nothing.
