# Contributing

Help is welcome. Without people to work with, updates stay rare, so contributors and maintainers make the biggest
difference to this project.

## Ways to help

- **Good bug reports**, with steps, logs and screenshots (see [SUPPORT.md](SUPPORT.md)). A clear report is a contribution.
- **Testing**: try new versions on your games and report what works and what does not.
- **Translations**: every visible text goes through `L.T` / `L.F`; the Italian table in
  `src/HybridLauncher.Core/Localization/Italian.cs` shows how a language is added.
- **Code**: fixes and features, see below.
- **Maintaining**: triaging issues, reviewing pull requests, keeping integrations up to date. Open an issue to
  introduce yourself if you are interested.

## Code contributions

1. For anything bigger than a small fix, open an issue first, so we agree on the approach before you spend
   time on it.
2. Fork, branch from `main`, keep the change focused on one thing.
3. Build it with `dotnet build src/HybridLauncher/HybridLauncher.csproj -c Release -p:Platform=x64` and run the self-test
   (`HybridLauncher.exe --selftest` on a scratch `HL_DATA` folder, see [docs/development.md](docs/development.md)).
4. Test on a scratch data folder and a test game folder, not only on your real games.
5. Open a pull request that says what changed, why, and how you tested it, with screenshots for anything visible.

## Ground rules

- OptiScaler is always installed through its own `setup_windows.bat`; no custom reimplementation of its installer.
- Every file the app changes in a game folder is backed up, and removing a mod restores the folder.
- No personal paths or machine-specific values in code, assets or builds.
- The app stays light, native (WinUI 3) and crash-free; new UI follows the existing style (dark, plain fills, no outlines).
- By contributing you agree that your contribution is provided under the same terms as the rest of the project
  (the GNU GPL v3, [LICENSE](LICENSE)), AS IS, without warranty.
- A fork you share stays under the GPL v3 and keeps the credit in [NOTICE.md](NOTICE.md).
