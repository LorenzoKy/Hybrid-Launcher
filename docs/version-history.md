# Version history: how each version was built

What changed for people is in the [changelog](../CHANGELOG.md). This page records **how** each released version was made:
the source it came from, the tools, the package, the checks it passed. Newest first. Add an entry for every release, and
keep the old ones as they are.

Crashes found along the way, and their fixes, are in [crashes.md](crashes.md).

---

## 1.1.0 — 2026-11-01 · Make it yours

| | |
|---|---|
| Tag | `v1.1.0` on `main` |
| Before it | `1.1.0-rc.1`, a release candidate built and tested locally only (never published) |
| Toolchain | .NET SDK 10.0.100 · `net10.0-windows10.0.22621.0` (app and core) · starter on .NET Framework 4.8 |
| UI | Windows App SDK 1.8 (WinUI 1.8.260803003, Foundation 1.8.260803002), Win2D 1.4.0 |
| Windows SDK build tools | 10.0.28000.2705 |
| Libraries | SteamKit2 3.5.0, MegaApiClient 1.10.5, SharpCompress 1.0.0, Newtonsoft.Json 13.0.3, QRCoder 1.6.0 |
| Package | `HybridLauncher-1.1.0-win-x64.zip`, 55,622,046 bytes (132 MB unpacked, 206 files) |
| SHA-256 | `4fbb355b081e7e81b226d4e0681992ed5a41283fdfd40cd7976af5439cb6f23d` |

**Built with** the same steps as 1.0.0 (see [Making a release](development.md#making-a-release)): `dotnet publish` of
`src/HybridLauncher` for `win-x64`, self-contained, `Release`, no debug symbols, into `app\`; the starter
(`src/HybridLauncher.Starter`) built with the same version and put at the top of the folder with `LICENSE` and
`NOTICE.md`; the folder zipped.

**Checked before release:**

- Build with 0 warnings and 0 errors; translation check: 0 missing Italian texts.
- Smoke tests `decide`, `updates` and `customdlls` (including a new check for an entry renamed by case only): all pass.
- Self-test of the whole app (every page, tab, theme, the feature picker and the tour) on fake games with sample
  downloads: 0 failures; then 20 repeated runs of library, Downloads, My PC and Settings: 0 crashes. The published
  `app\HybridLauncher.exe` itself reported version 1.1.0 and passed a self-test.
- The audit found and fixed one bug (the search box took the focus after Downloads) and one crash brought in by the first
  fix for it; both are in [crashes.md](crashes.md).
- No personal paths and no tool-specific files in the repository.

**Update path:** 1.0.0 sees 1.1.0 as the latest GitHub release and offers it a few seconds after it starts.

---

## 1.0.0 — 2026-10-31 · The first release

| | |
|---|---|
| Tag | `v1.0.0` on `main` |
| Toolchain | Same as 1.1.0: the project files differ only in the version number |
| Package | `HybridLauncher-1.0.0-win-x64.zip`, 55,498,701 bytes |
| SHA-256 | `cedaf8856cca0cfc6c9157009ba4f26c1236ffa111f6ac31e8b2096279b29366` |

**Built with** the steps in [Making a release](development.md#making-a-release): self-contained `win-x64` publish into
`app\`, the starter at the top, `LICENSE` and `NOTICE.md`, zipped.

**Notes:** the first public version. The repository history was started fresh as one 1.0.0 commit; the license is the
GPL v3 with the credit term in [NOTICE.md](../NOTICE.md).
