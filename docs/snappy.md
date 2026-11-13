# Snappy: a pillar of Hybrid Launcher

Hybrid Launcher must feel immediate. A page never visibly loads: what was shown before comes back as it was left, at once,
and what is new arrives without freezing the frames around it. This file holds the rules every version keeps, the way it is
measured, and the gate that stops a slower version from shipping. Since 2.0.0-rc.9.

## The rules

1. **A return never rebuilds.** A game page, the library and Settings stay in memory (MainWindow.GameSlots, the Frame's
   NavigationCacheMode). Going back shows them as they were left. Only a real reason builds them again: a new look (theme,
   palette), the Features page, a game whose folder, exe or install state changed.
2. **Caches know what changed, not only that something changed.** `Helpers.SettingsKeys.Parts` hashes every top-level setting
   (and each game under `Games/<id>`). A kept page compares the parts it was built with and learns which settings moved. What
   the app writes by itself while merely used (the window, the tab and game opened last, the "already seen" marks of update
   checks and notifications, first-start steps) is listed in `SettingsKeys.Volatile` and never counts. When a setting a page may
   read did change, the page is shown as it was and brought up to date in place after its first frame (`GamePage.RefreshInPlace`),
   one part per turn of the UI thread. Until rc.8 any save at all (an OptiScaler update check) rebuilt every page on its next visit.
   A new setting the app writes on its own, that no page shows, goes in `Volatile`.
3. **Big content arrives in turns.** Anything with many cards or rows is added one or two pieces per turn of the UI thread
   (`DispatcherQueuePriority.Low`), the first part with the click: the Dashboard's cards (`DashboardView.RevealInTurns`), the
   OptiScaler tab's sections, the Mods tab's sections, the OptiScaler.ini cards (`GamePage.Masonry`), the release notes
   (`OptiNotesView`). Folded content is built when it is opened. WinUI lays out everything added in one turn before it draws
   the next frame: 300 ms of layout is 300 ms of a frozen window.
4. **The first frame of a game switch carries only the banner, the launch bar and the tab row.** Everything else runs after it
   (`GamePage.AfterFirstFrame`, `_deferViews`): the open tab's content, OptiScaler.ini, the folder footprint.
5. **No file, network, process or parsing work on the UI thread.** Read and parse in `Task.Run` or after `ConfigureAwait(false)`
   in Core (OptiReleases, GameEvents, Luma), then touch the UI. Results that depend on a file are kept until the file changes
   (size and last write, e.g. `DlssNr.Mentions`). One process snapshot per question, never one per exe name.
6. **Idle time prepares what comes next.** While nobody uses the window, the pages of the games likely to be opened next are
   made unseen (MainWindow.PrepareStep) and the info tabs of the open game are laid out in a hidden layer (GamePage.WarmStep).
   Never while the user scrolls or clicks.
7. **Every slow spot gets a name.** Wrap UI-thread work in `UiPerf.Scope("Class.What")`; in detailed mode `UiPerf.Layout(el,
   "what")` lays out right after a change so its layout time shows under that name instead of "no scope".

## How it is measured

`Tools\progetti\mod-manager\run-perf.ps1 -Only snappy -Exe <dist>\app\HybridLauncher.exe -Report -Gate`

No mouse or keys (safe while the PC is in use), on a copy of the real data in %TEMP%\hl-perf. It opens six games (first visit,
then a return to each), three games prepared while idle, every tab of a game, the library, Settings, and scrolls the long pages.
Each action is timed to its first frame, then watched for 1.5 s: frames missed and the time lost in them (jank). Budgets per
kind are in `MainWindow.Snappy.cs` (`SnapBudgets`).

To find what a slow action does: add `-Trace` and read the trace with `Tools\dev\analyze-ui-trace` (`--thread` is the native
UI thread id from the log's "UI thread" line, `--log` the run's log, `--from` the time of "Snappy:").

## The gate

`-Gate` compares the run's medians per kind (first frame, jank) with the newest entry of `docs/perf-history.md`. Worse by more
than 25% and by more than the noise (15 ms first frame, 40 ms jank) is a regression: exit code 2, and `check-release.ps1`
fails its "snappy" check (it measures a second time before it counts one). Every release candidate is measured with `-Report`,
so the history always holds the version to beat. A regression is fixed before the version ships, never written off.

## Where it stood at rc.9

Returns: first frame 5-12 ms, jank about 30 ms (rc.8: up to 291 ms and 692 ms after a settings save). Pages prepared while
idle: first frame about 10 ms (rc.8: 155 ms, they were rebuilt). First visits: first frame about 100 ms, worst gap under
250 ms (rc.8: up to 460 ms). Still over budget: the jank of a first visit (about 500 ms spread over the 1.5 s after it), and
Settings' first opening (about 155 ms).
