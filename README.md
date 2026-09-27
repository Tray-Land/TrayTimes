# Tray Times

Click the tray icon for live Milwaukee County Transit System (MCTS) bus arrivals. Click away and the flyout closes. Hovering over the icon shows the next arrivals at your first three favorite stops.

- **Favorites:** your favorite stops, with arrivals, route lines, and the approaching buses on the map. Pick stops in Settings (Route → Direction → Stop, or tap a stop on the map), or star one from the other tabs.
- **Commute:** the buses for your likely next trip. Away from home, it shows the buses that take you home; at home, the buses to work (or school, or your first custom place). It lists only routes that get there in one ride, heading the right way, within about a 10-minute walk at each end. If Windows location is off, it guesses from the time of day (mornings start at home). "Going to" picks a different place for now. Set your favorite places (Home, Work, School, and any custom ones) in Settings.
- **Nearby:** the same view around your current location, from Windows location services. It dims buses that will leave before you can walk to the stop.
- **Arrivals and bus locations:** the MCTS BusTime Developer API v3 (`realtime.ridemcts.com`). The API guide is `../DeveloperAPIGuide3_0.pdf`.
- **Stop directory:** BusTime can't search stops by location. The app crawls every route's stops about once a week (around 100 requests) and caches them in LocalState. Settings has an "Update now" button.
- **Scheduled times:** MCTS's GTFS timetable (`kamino.mcts.org/gtfs/google_transit.zip`, about 9 MB). It's downloaded to LocalState, checked for updates weekly, and read into memory while the flyout is open. Where BusTime has no prediction (it only looks about 30 to 60 minutes ahead), rows show the scheduled time instead, labeled "Scheduled" and without the tracking bell. The GTFS route and direction IDs are matched to BusTime's using the stop directory.
- **Basemap and address search:** Azure Maps, with the map rendered by [WinUIEx.Maps](https://github.com/dotMorten/WinUIEx.Maps).

The transit code sits behind `ITransitService` (`TrayTimes.Core/Transit/`), so another provider such as Transit.app can be added later without changing the UI.

## Project layout

- `TrayTimes/`: the WinUI 3 app. `Views/` and `Controls/` hold the UI; `Services/` holds code that needs Windows (settings storage, location, the stop directory cache, tray placement).
- `TrayTimes.Core/`: plain .NET code with no WinUI or Windows dependencies. `Transit/` has the provider-neutral models, the BusTime client, and the GTFS timetable reader; `Models/` has the favorite stops and places and the arrival snapshots; `Commute/` works out the likely next trip and its direct routes.
- `TrayTimes.Tests/`: MSTest unit tests for `TrayTimes.Core`, laid out to mirror it. Run them with `dotnet test TrayTimes.Tests`. They need no API key or package identity.

## Setup

1. In `TrayTimes/`, copy `OAuth.resw.sample` to `OAuth.resw`. That file is gitignored.
2. Put your MCTS BusTime key in `MctsApiKey`. Optionally, put an Azure Maps key in `AzureMapsKey` for the basemap and address search.
3. Run from `TrayTimes/` with `dotnet run -c Debug -p:Platform=x64` (or `ARM64`). Tray apps keep running after `dotnet run` returns only when launched detached: `winapp run . --detach --arch x64`.

## API budget

The default key allows 10,000 requests a day. While the flyout is open, arrivals refresh every 30 s and bus positions every 15 s. A minute after it's dismissed the flyout closes and nothing refreshes, unless you're tracking a bus (the bell next to an arrival): then only that stop refreshes, every 60 s, then every 30 s under 10 minutes out and every 10 s under 5, until 5 minutes after the bus is due.
