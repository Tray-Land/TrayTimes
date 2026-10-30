@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app following the `winui-tray-app` skill's patterns. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or secrets.

- Launch with `winapp run` (or `dotnet run`); never register the package by hand. Use `winapp run --clean` to test first-run behavior.
- `App.xaml.cs` owns the tray icon, single instance, and the only exit path (the Exit menu). There is no main window.
- The flyout (`Views/TimesFlyoutWindow`) is hidden on dismiss (`HidePopup`) and closed after a minute hidden (`CloseWindow`). Anything it starts or subscribes to, it must stop in `HidePopup` / `CloseWindow`.
- Network work runs only while the flyout is visible: each monitor polls only while its `IsForeground` is true. The exceptions are the work the tray icon itself displays, a tracked bus (`BusTrackingService`) and an active trip (`ActiveTripService`), documented in `App`.
- App API keys go in `TrayTimes/OAuth.resw` (gitignored) and are read through `Services/Secrets`. Nothing secret goes in `SettingsService`.
- The repo is MIT licensed (`LICENSE.txt`). Keep `README.md` in step with the features (what it does, install, setup/keys, privacy, build from source, license).
- Put pure decision logic (parsing, policies, formatting) in `TrayTimes.Core`, which has no WinRT dependency, so `TrayTimes.Tests` can cover it.
- Releases: bump `Identity/@Version`, run `build-msix-for-gh.ps1`, publish the draft without changing its tag, then run `build-msix-for-gh.ps1 -VerifyPublished`. The script is a copy of the `winui-tray-app` skill's; fix it there and recopy, never here.
