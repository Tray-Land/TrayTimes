# Tray Times

Click the tray icon for live Milwaukee County Transit System (MCTS) bus arrivals. Click away and the flyout closes. Hovering over the icon shows the next arrivals at your first three saved stops.

- **Saved:** your stops, with arrivals, route lines, and the approaching buses on the map. Pick stops in Settings (Route → Direction → Stop, or tap a stop on the map), or star one from the other tabs.
- **Home / Work:** the stops closest to your home and work addresses, with walking distance, the lines that serve each stop, and live arrivals. Set the addresses in Settings.
- **Nearby:** the same view around your current location, from Windows location services. It dims buses that will leave before you can walk to the stop.
- **Arrivals and bus locations:** the MCTS BusTime Developer API v3 (`realtime.ridemcts.com`). The API guide is `../DeveloperAPIGuide3_0.pdf`.
- **Stop directory:** BusTime can't search stops by location. The app crawls every route's stops about once a week (around 100 requests) and caches them in LocalState. Settings has an "Update now" button.
- **Basemap and address search:** Azure Maps, with the map rendered by [WinUIEx.Maps](https://github.com/dotMorten/WinUIEx.Maps).

The transit code sits behind `ITransitService` (`TrayTimes.Core/Transit/`), so another provider such as Transit.app can be added later without changing the UI.

## Project layout

- `TrayTimes/`: the WinUI 3 app. `Views/` and `Controls/` hold the UI; `Services/` holds code that needs Windows (settings storage, location, the stop directory cache, tray placement).
- `TrayTimes.Core/`: plain .NET code with no WinUI or Windows dependencies. `Transit/` has the provider-neutral models and the BusTime client; `Models/` has the saved stops, places, and arrival snapshots.
- `TrayTimes.Tests/`: MSTest unit tests for `TrayTimes.Core`, laid out to mirror it. Run them with `dotnet test TrayTimes.Tests`. They need no API key or package identity.

## Setup

1. In `TrayTimes/`, copy `OAuth.resw.sample` to `OAuth.resw`. That file is gitignored.
2. Put your MCTS BusTime key in `MctsApiKey`. Optionally, put an Azure Maps key in `AzureMapsKey` for the basemap and address search.
3. Run from `TrayTimes/` with `dotnet run -c Debug -p:Platform=x64` (or `ARM64`). Tray apps keep running after `dotnet run` returns only when launched detached: `winapp run . --detach --arch x64`.

## API budget

The default key allows 10,000 requests a day. While the flyout is closed, arrivals refresh every 60 s (about 1,440 a day). While it's open, arrivals refresh every 30 s and bus positions every 15 s.
