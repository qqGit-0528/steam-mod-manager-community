<p align="center">
  <img src="docs/assets/logo.png" alt="Steam Mod Manager" width="128" />
</p>

<h1 align="center">Steam Mod Manager</h1>

<p align="center">
  <strong>A desktop mod manager for Paradox games and Steam Workshop games.</strong>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet" alt=".NET 10" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/Avalonia-12.1-8B44AC" alt="Avalonia 12.1" /></a>
  <img src="https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-lightgrey" alt="Platforms" />
  <a href="https://github.com/zxxwyt/steam-mod-manager-community/actions/workflows/build.yml"><img src="https://github.com/zxxwyt/steam-mod-manager-community/actions/workflows/build.yml/badge.svg" alt="Build and test" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> | <a href="README.zh-CN.md">简体中文</a>
</p>

---

## What is Steam Mod Manager?

Steam Mod Manager is a desktop tool for managing game mods without leaving a single window. It discovers installed Steam games, scans local and Workshop content, searches the Steam Workshop, and keeps mod lists organized per game.

Switch games from the sidebar and the whole workspace follows: local mods, Workshop browsing, version filters, run profiles, and the **Launch Game** action. Use **Apply to game** to write a profile to the game launcher.

## The problem

Steam Workshop listings do not always make it clear whether a mod matches your installed game version. Many players subscribe, wait for the download to finish, launch the game, and only then discover that the mod targets a different version — sometimes after a save is already broken, and often followed by disabling mods one by one to find the culprit.

Steam Mod Manager reads the `supported_version` declared in each mod's descriptor before you subscribe or enable it. Workshop results, the local mod list and run profiles show a clear **Compatible / Incompatible / Unknown** label, so version mismatches are visible before download instead of after launch. It is a declared-version check, not a runtime guarantee, but it makes the most common Workshop surprise visible up front.

## Features

### Game discovery

- Reads every Steam library and installed-game manifest on startup.
- Adds a game only when it supports the Steam Workshop, detected from a local Workshop directory, `appworkshop_<AppId>.acf`, or the Steam store Workshop category.
- Finds Steam through the Windows registry (including installs outside Program Files), the default locations, every library in `libraryfolders.vdf`, and Steam paths saved for other games.
- Ignores folders that only look like a Steam install, such as a `%LOCALAPPDATA%\Steam` cache directory without `steamapps`.
- Keeps separate paths, profiles, search state and version selection for each game.

### Mod library

- Scans local mods and Workshop downloads, reading version declarations and descriptor dependencies.
- In the CK3 library, choose **Install local mod** to preview and install one mod from a ZIP or folder containing `descriptor.mod`.
- Updates the mod list automatically when files are added or removed.
- Flags multiple available copies of the same Workshop mod. Hover over the label to see their versions, sources and paths. Same-name copies with incomplete identities are marked as possible duplicates.
- Deletes a selected installation from disk after confirmation. For supported Steam games, Workshop removal also unsubscribes the item.
- Shows declared compatibility as **Compatible**, **Incompatible**, or **Unknown** with green, red and grey labels.

### Steam Workshop

- Searches Workshop items in-app by name, category, declared game version, sorting and pagination.
- Jump directly to a page by number; out-of-range values are clamped.
- Previews cover art, author information and version variants inside the app.
- Aggregates releases with the same game, author and title while keeping localizations and patches from other authors separate.

### Run profiles

- Add, remove and enable or disable mods for each game. Drag the handle at the start of a row to reorder it, or use the move buttons.
- In CK3, **Check file conflicts** lists shared paths and content differences in configured load order. It also reports duplicate top-level trait keys under `common/traits` and descriptor `replace_path` directives.
- **Check dependencies** reports missing, disabled, ambiguous and cyclic dependencies. Confirm the preview to queue missing Workshop items.
- Profile import shows the requested and installed mod versions. Choose a local copy explicitly when versions differ, or leave the entry pending.
- CK3 profiles export to `.ck3pack` with enabled mod files, declared versions, order and toggles. A recipient on the same CK3 version can verify and restore a separate profile, then choose whether to apply it. Disabled items are kept as list entries.
- **Apply to game** writes the current profile to the game launcher playset.
- **Launch Game** verifies the active game profile and applies pending changes before launching.

### Downloads

- Subscribes and downloads through the local, already signed-in Steam client.
- Shows real Steam byte progress, speed and installation results.
- Keeps one active task per game and Workshop item, with retry, update checks, and reuse of installed Steam content. Removing a task keeps its Steam files.

### Language and CK3 diagnostics

- Use **Settings → Interface language** to switch between Simplified Chinese and English. The selection applies across games and is saved for the next launch.
- In the CK3 sidebar, open **CK3 diagnostics** and choose **Read logs** to view the latest session logs and up to five recent crash reports. Select a source to read the log tail, check its timestamp, or open its folder.

### Compatibility

Compatibility labels are based on the mod's declared `supported_version`, not on runtime testing. A green label means the declared version prefix matches the current game version; red means it does not; grey means the version is unknown or cannot be compared.

## Quick start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A Steam installation and one of the supported games for full functionality

### Run from source

```shell
dotnet restore SteamModManager.sln
dotnet run --project src/Ck3ModManager.Desktop
```

On macOS or Linux, you can also run `bash scripts/run-desktop.sh`.

### Build the macOS app

```shell
bash scripts/package-macos-app.sh
```

The script produces `artifacts/macos-app/Steam Mod Manager.app`, a self-contained arm64 bundle.

### CLI diagnostics

```shell
dotnet run --project src/Ck3ModManager.Cli -- doctor
dotnet run --project src/Ck3ModManager.Cli -- workshop 2273832430
dotnet run --project src/Ck3ModManager.Cli -- browse RICE
dotnet run --project src/Ck3ModManager.Cli -- profiles
dotnet run --project src/Ck3ModManager.Cli -- download <workshop-id>
dotnet run --project src/Ck3ModManager.Cli -- games
```

`doctor` is read-only. The default game is `ck3`; prefix a command with `--game <id>` to change it.

## Build and test

```shell
dotnet build SteamModManager.sln --configuration Release
dotnet test --solution SteamModManager.sln --configuration Release --no-build
```

Tests use Microsoft.Testing.Platform and cover Core/Application units, Steam HTTP and file-system fixtures, and headless Avalonia UI tests. They do not require a game installation, network access, or changes to real game files.

GitHub Actions builds and tests on Windows, macOS and Linux. These are development builds; no signed installer is published yet.

## Documentation

- [Architecture](docs/architecture.md)
- [Workshop cache and loading](docs/workshop-cache.md)
- [Steam client download and progress](docs/workshop-download.md)
- [Enable/disable and playset application](docs/game-mod-activation.md)
- [Research and PCL workflow reference](docs/research.md)
- [Contributing and PR guidelines](CONTRIBUTING.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## Contributing

Issues and pull requests are welcome in English or Simplified Chinese. Players can share reproducible bug reports, mod authors can contribute small conflict and dependency examples, and contributors can help with code, tests, translations, accessibility, or documentation.

See the [contributor quick start](CONTRIBUTING.md#contributor-quick-start) before opening a pull request.

## License

Project-owned code is licensed under the [MIT License](LICENSE). Third-party components retain their own licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Acknowledgements

PCL (Plain Craft Launcher) is a reference for product patterns and architecture. The interface borrows its sidebar, search cards, resource list and in-app detail flow conceptually; all Avalonia components are independently written.

## Naming

The product name is **Steam Mod Manager**. The solution is `SteamModManager.sln`. Internal .NET namespaces and the application-data directory retain their historical `Ck3ModManager` names for compatibility with existing settings and profiles.
