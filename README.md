# Quanta · Memory Core

A futuristic, game-aware RAM optimizer for Windows 10/11, built with WPF on .NET 8.

- **Animated HUD dashboard**: memory gauge, particle network, live telemetry, scrolling timeline
- **Low GPU cost**: frame-capped (24/30/60 FPS), static layers bitmap-cached, and all animation stops when Quanta is minimized, in the tray, or behind a running game. An optional CPU-only renderer uses no GPU at all.
- **Game Shield**: detects running games (known titles, your list, Steam/Epic/GOG/Xbox/Riot/EA/Ubisoft libraries, borderless fullscreen windows). It never trims the game, its child processes, its install folder, overlays or anti-cheat. While gaming, system-wide cache purges drop to the low-priority-only purge so the game's cached assets survive. You can override this per purge or permanently.
- **Profiles**: Quick, Balanced, Deep and Custom (working-set trim, low-priority / full standby purge, modified-list flush, file-cache flush, page combining)
- **Automation**: threshold trigger, interval purge, a global hotkey (Ctrl+Alt+M, with fallbacks if it's taken), and a tray icon with a live load ring
- **Process matrix**: per-process exclude, mark as game, or trim now
- **Self-updater**: checks GitHub Releases, verifies the download's SHA-256, swaps the exe and restarts

## Install

Download `QuantaRAM.exe` from [Releases](https://github.com/GPTmadeit/QuantaRAM/releases) and run it.
It needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).

Quanta runs unelevated by default and can trim most of your own processes that way. Click **ELEVATE** to unlock standby, file-cache and modified-list purges and page combining.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
```

## Releasing an update

1. Bump `<Version>` in `QuantaRAM.csproj`. The release workflow also stamps the version from the tag.
2. Commit, then tag and push:
   ```powershell
   git tag v1.2.0
   git push origin main v1.2.0
   ```
3. GitHub Actions builds `QuantaRAM.exe` and `QuantaRAM.exe.sha256` and attaches them to the release.
   Running copies of Quanta find the update within 6 hours, or straight away with **Settings → Updates → Check now**.

Settings live in `%AppData%\QuantaRAM\settings.json`.
