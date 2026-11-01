# Crashes and how they were fixed

A standing record of every crash found in Hybrid Launcher: what made it crash, how it was found, how it was fixed and how
the fix was checked. Newest first. Add an entry for every crash, even one that never reached a release, so the same mistake
is not made twice.

Each entry: **when and in which version**, **what you see**, **cause**, **how it was found**, **fix**, **how the fix was
checked**.

---

## Moving the focus while the page changes (1.1.0, before release)

- **When:** 2026-11-01, while preparing 1.1.0. Never in a published build.
- **What you see:** the app closes by itself a second or two after it starts, or when you go to another page. Exit code
  `0xC000027B` (an unhandled WinUI error), nothing in the app's log, no Windows crash report.
- **Cause:** the fix for the search box bug below first ran on `ContentFrame.Navigating`, which fires while the frame is
  swapping pages. Moving the focus at that moment (even queued on the dispatcher) sometimes hit the frame halfway through
  the swap, and WinUI crashed.
- **How it was found:** the audit ran the self-test many times in a row instead of once. With that first fix:
  2 crashes in 19 runs. Without it: 0 in 15. So the fix itself caused the crash.
- **Fix:** `MainWindow.GuardSearchFocus` only *reads* who has the focus on `Navigating`, and moves it on `Navigated`,
  once the new page is in place.
- **Checked:** 20 runs of the self-test (library, Downloads with sample downloads, My PC, Settings) with 0 crashes and 0
  failed steps, then the full self-test with 0 failures.
- **Rule:** in WinUI, do not move the focus (or change the visual tree) inside `Navigating`; do it on `Navigated` or
  later. A crash that happens 1 time in 10 needs 10 or more runs to show up: run a self-test several times before a
  release.

## Related bug (not a crash): the search box took the focus after Downloads

- **When:** found 2026-11-01 in the 1.1.0 audit; the gap was already in 1.0.0.
- **What you see:** after leaving the Downloads page while downloads are listed, the game list's search box looks
  clicked (caret on, focus in it), and typing goes into it.
- **Cause:** the guard that keeps the focus off the search box ran only for a click in the side list. A page changed in
  any other way (a button that opens Settings, My PC, a game) and that had a focused control (a download's pause button)
  left WinUI to hand the focus to the first control of the window: the search box.
- **How it was found:** the self-test with `-Samples` (pretend downloads) failed every step after Downloads with "the
  game list's search box took the focus by itself"; without samples, 0 failures.
- **Fix:** the guard now runs on every page change of the content frame (see the crash above for where it must run).
- **Checked:** the same runs as above.

## Not reproduced

- **2026-11-01, 1.1.0 audit:** one self-test run stopped with "The WinUI Desktop Window object has already been closed"
  in the middle of the run. The window was closed from outside (it was on screen while the PC was in use); it did not
  happen again in more than 40 runs.

---

## How to look for crashes

1. Build Release and run the self-test (see [development.md](development.md#run-on-test-data)) **several times**, with
   and without `-Samples`, and with fake games.
2. Watch the exit code: `0` is a clean run. `-1073741189` (`0xC000027B`) is a WinUI crash; `-1073740791`
   (`0xC0000409`) is a fail-fast.
3. Keep each run's log (`-Data` to a separate folder per run): the last line shows how far the app got.
4. Compare with the change removed before blaming it, and after fixing, run the same loop again.
