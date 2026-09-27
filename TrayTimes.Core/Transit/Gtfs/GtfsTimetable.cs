using System.Globalization;
using System.IO.Compression;
using TrayTimes.Models;

namespace TrayTimes.Transit.Gtfs;

/// <summary>
/// An agency's published timetable, read from its GTFS feed, for showing scheduled times where
/// real-time predictions don't reach. Route and direction IDs are translated to the real-time
/// feed's (BusTime's) using the stop directory, so scheduled and predicted arrivals line up.
/// </summary>
public sealed class GtfsTimetable
{
    private readonly TimeZoneInfo _timeZone;
    private readonly GtfsCalendar _calendar;
    private readonly string[] _serviceIds;
    private readonly Trip[] _trips;
    private readonly Dictionary<string, StopTime[]> _stopTimes;

    private GtfsTimetable(TimeZoneInfo timeZone, GtfsCalendar calendar, string[] serviceIds, Trip[] trips, Dictionary<string, StopTime[]> stopTimes)
    {
        _timeZone = timeZone;
        _calendar = calendar;
        _serviceIds = serviceIds;
        _trips = trips;
        _stopTimes = stopTimes;
    }

    /// <summary>Reads a GTFS zip.</summary>
    /// <param name="directory">
    /// Every stop with the real-time feed's routes and directions, used to translate the timetable's
    /// IDs. Without it, arrivals keep the GTFS route IDs and "0"/"1" direction IDs.
    /// </param>
    /// <exception cref="InvalidDataException">The zip or one of its tables is malformed.</exception>
    public static GtfsTimetable Load(Stream zipStream, IReadOnlyList<IndexedStop> directory)
    {
        using ZipArchive zip = new(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        Dictionary<string, IReadOnlyList<StopRouteInfo>> directoryRoutes = [];
        foreach (IndexedStop stop in directory)
        {
            directoryRoutes.TryAdd(stop.Id, stop.Routes);
        }

        TimeZoneInfo timeZone = ReadTimeZone(zip);
        GtfsCalendar calendar = new();
        WithTable(zip, "calendar.txt", required: false, calendar.ReadCalendar);
        WithTable(zip, "calendar_dates.txt", required: false, calendar.ReadCalendarDates);

        Dictionary<string, RouteNames> routes = ReadRoutes(zip, directoryRoutes.Values);
        TripTable trips = ReadTrips(zip, routes);
        Dictionary<string, StopTime[]> stopTimes = ReadStopTimes(zip, trips, directoryRoutes);
        return new GtfsTimetable(timeZone, calendar, [.. trips.ServiceIds], [.. trips.Trips], stopTimes);
    }

    /// <summary>Scheduled arrivals at the given stops between two times, soonest first.</summary>
    public IReadOnlyList<Arrival> GetArrivals(IEnumerable<string> stopIds, DateTimeOffset from, DateTimeOffset until)
    {
        List<(string StopId, StopTime[] Times)> stops = [];
        foreach (string stopId in stopIds.Distinct())
        {
            if (_stopTimes.TryGetValue(stopId, out StopTime[]? times))
            {
                stops.Add((stopId, times));
            }
        }

        List<Arrival> arrivals = [];
        if (stops.Count == 0)
        {
            return arrivals;
        }

        // Times past midnight (e.g. 25:10) belong to the previous day's service.
        DateOnly last = LocalDate(until);
        for (DateOnly date = LocalDate(from).AddDays(-1); date <= last; date = date.AddDays(1))
        {
            bool[] running = [.. _serviceIds.Select(id => _calendar.IsActive(id, date))];
            DateTimeOffset origin = ServiceDayOrigin(date);
            foreach ((string stopId, StopTime[] times) in stops)
            {
                foreach (StopTime time in times)
                {
                    Trip trip = _trips[time.Trip];
                    DateTimeOffset at = origin.AddSeconds(time.Seconds);
                    if (running[trip.Service] && at >= from && at <= until)
                    {
                        arrivals.Add(ToArrival(stopId, trip, time, at));
                    }
                }
            }
        }

        arrivals.Sort((a, b) => a.PredictedTime.CompareTo(b.PredictedTime));
        return arrivals;
    }

    private static Arrival ToArrival(string stopId, Trip trip, StopTime time, DateTimeOffset at) => new(
        stopId,
        trip.RouteId,
        trip.RouteDisplayId,
        trip.DirectionId,
        trip.Destination,
        VehicleId: null,
        at,
        IsDue: false,
        IsDelayed: false,
        IsDeparture: time.Sequence == trip.FirstSequence,
        // A bus ending its trip here can't be boarded.
        time.Sequence == trip.LastSequence ? ArrivalStatus.DropOffOnly : ArrivalStatus.Scheduled,
        IsRealTime: false);

    private DateOnly LocalDate(DateTimeOffset time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, _timeZone).DateTime);

    /// <summary>GTFS times count from "noon minus 12 hours", which differs from midnight on DST change days.</summary>
    private DateTimeOffset ServiceDayOrigin(DateOnly date)
    {
        DateTime noon = date.ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(noon, _timeZone.GetUtcOffset(noon)).AddHours(-12);
    }

    private static TimeZoneInfo ReadTimeZone(ZipArchive zip)
    {
        string? id = null;
        WithTable(zip, "agency.txt", required: true, csv =>
        {
            int column = csv.RequiredColumn("agency_timezone");
            if (csv.Read())
            {
                id = csv[column];
            }
        });

        try
        {
            return string.IsNullOrEmpty(id) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// Each GTFS route's real-time ID and display name. The IDs usually match, but some GTFS routes
    /// carry a suffix (MCTS's "43S") that the real-time feed shows by its short name ("43").
    /// </summary>
    private static Dictionary<string, RouteNames> ReadRoutes(ZipArchive zip, IEnumerable<IReadOnlyList<StopRouteInfo>> directoryRoutes)
    {
        Dictionary<string, string> displayIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (StopRouteInfo info in directoryRoutes.SelectMany(r => r))
        {
            displayIds.TryAdd(info.RouteId, info.RouteDisplayId);
        }

        Dictionary<string, RouteNames> routes = [];
        WithTable(zip, "routes.txt", required: true, csv =>
        {
            int id = csv.RequiredColumn("route_id");
            int shortName = csv.Column("route_short_name");
            while (csv.Read())
            {
                string gtfsId = csv[id];
                string name = csv[shortName] is { Length: > 0 } s ? s : gtfsId;
                string routeId = displayIds.ContainsKey(gtfsId) || !displayIds.ContainsKey(name) ? gtfsId : name;
                routes[gtfsId] = new RouteNames(routeId, displayIds.GetValueOrDefault(routeId, name));
            }
        });
        return routes;
    }

    private static TripTable ReadTrips(ZipArchive zip, Dictionary<string, RouteNames> routes)
    {
        TripTable table = new();
        WithTable(zip, "trips.txt", required: true, csv =>
        {
            int tripId = csv.RequiredColumn("trip_id");
            int routeId = csv.RequiredColumn("route_id");
            int serviceId = csv.RequiredColumn("service_id");
            int direction = csv.Column("direction_id");
            int headsign = csv.Column("trip_headsign");
            Dictionary<string, int> services = [];
            while (csv.Read())
            {
                RouteNames route = routes.GetValueOrDefault(csv[routeId]) ?? new RouteNames(csv[routeId], csv[routeId]);
                string service = csv[serviceId];
                if (!services.TryGetValue(service, out int serviceIndex))
                {
                    serviceIndex = table.ServiceIds.Count;
                    services[service] = serviceIndex;
                    table.ServiceIds.Add(service);
                }

                table.Add(csv[tripId], new Trip(route.RouteId, route.DisplayId, csv[direction], CleanHeadsign(csv[headsign]), serviceIndex));
            }
        });
        return table;
    }

    /// <summary>
    /// Every stop's visits, plus a vote on which real-time direction each GTFS direction of a route
    /// is: a stop the directory lists under only one direction of that route votes for it.
    /// </summary>
    private static Dictionary<string, StopTime[]> ReadStopTimes(ZipArchive zip, TripTable trips, Dictionary<string, IReadOnlyList<StopRouteInfo>> directoryRoutes)
    {
        Dictionary<string, List<StopTime>> byStop = [];
        Dictionary<(string RouteId, string GtfsDirection), Dictionary<string, int>> votes = [];
        WithTable(zip, "stop_times.txt", required: true, csv =>
        {
            int tripId = csv.RequiredColumn("trip_id");
            int stopId = csv.RequiredColumn("stop_id");
            int sequence = csv.RequiredColumn("stop_sequence");
            int departure = csv.Column("departure_time");
            int arrival = csv.Column("arrival_time");
            while (csv.Read())
            {
                string stop = csv[stopId];
                int seconds = ParseTime(csv[departure]) is var d and >= 0 ? d : ParseTime(csv[arrival]);
                if (!trips.TryGetIndex(csv[tripId], out int index)
                    || stop.Length == 0
                    || seconds < 0
                    || !int.TryParse(csv[sequence], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq))
                {
                    continue;
                }

                Trip trip = trips.Trips[index];
                trip.FirstSequence = Math.Min(trip.FirstSequence, seq);
                trip.LastSequence = Math.Max(trip.LastSequence, seq);
                if (!byStop.TryGetValue(stop, out List<StopTime>? times))
                {
                    times = [];
                    byStop[stop] = times;
                }

                times.Add(new StopTime(index, seconds, seq));

                if (directoryRoutes.TryGetValue(stop, out IReadOnlyList<StopRouteInfo>? served) && OnlyDirection(served, trip.RouteId) is { } realTimeDirection)
                {
                    if (!votes.TryGetValue((trip.RouteId, trip.GtfsDirectionId), out Dictionary<string, int>? tally))
                    {
                        tally = [];
                        votes[(trip.RouteId, trip.GtfsDirectionId)] = tally;
                    }

                    tally[realTimeDirection] = tally.GetValueOrDefault(realTimeDirection) + 1;
                }
            }
        });

        foreach (Trip trip in trips.Trips)
        {
            if (votes.TryGetValue((trip.RouteId, trip.GtfsDirectionId), out Dictionary<string, int>? tally))
            {
                trip.DirectionId = tally.MaxBy(v => v.Value).Key;
            }
        }

        return byStop.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }

    /// <summary>The route's direction at a stop, if the stop is served in just one.</summary>
    private static string? OnlyDirection(IReadOnlyList<StopRouteInfo> served, string routeId)
    {
        string? found = null;
        foreach (StopRouteInfo info in served)
        {
            if (!string.Equals(info.RouteId, routeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null && !string.Equals(found, info.DirectionId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            found = info.DirectionId;
        }

        return found;
    }

    /// <summary>"H:MM:SS" (hours may pass 24) as seconds, or -1.</summary>
    private static int ParseTime(string value)
    {
        string[] parts = value.Split(':');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int s)
            ? (h * 3600) + (m * 60) + s
            : -1;
    }

    /// <summary>"TO BAYSHORE - SILVER SPRING" reads as "BAYSHORE - SILVER SPRING" next to a route number.</summary>
    private static string CleanHeadsign(string headsign) =>
        headsign.StartsWith("TO ", StringComparison.OrdinalIgnoreCase) ? headsign[3..].TrimStart() : headsign;

    private static void WithTable(ZipArchive zip, string name, bool required, Action<GtfsCsvReader> read)
    {
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            if (required)
            {
                throw new InvalidDataException($"The schedule has no {name}.");
            }

            return;
        }

        using StreamReader reader = new(entry.Open());
        read(new GtfsCsvReader(reader, name));
    }

    private sealed record RouteNames(string RouteId, string DisplayId);

    private readonly record struct StopTime(int Trip, int Seconds, int Sequence);

    private sealed class Trip(string routeId, string routeDisplayId, string gtfsDirectionId, string destination, int service)
    {
        public string RouteId { get; } = routeId;

        public string RouteDisplayId { get; } = routeDisplayId;

        public string GtfsDirectionId { get; } = gtfsDirectionId;

        /// <summary>The real-time feed's direction ID once resolved; the GTFS one until then.</summary>
        public string DirectionId { get; set; } = gtfsDirectionId;

        public string Destination { get; } = destination;

        public int Service { get; } = service;

        public int FirstSequence { get; set; } = int.MaxValue;

        public int LastSequence { get; set; } = int.MinValue;
    }

    private sealed class TripTable
    {
        private readonly Dictionary<string, int> _indexes = [];

        public List<Trip> Trips { get; } = [];

        public List<string> ServiceIds { get; } = [];

        public void Add(string tripId, Trip trip)
        {
            if (_indexes.TryAdd(tripId, Trips.Count))
            {
                Trips.Add(trip);
            }
        }

        public bool TryGetIndex(string tripId, out int index) => _indexes.TryGetValue(tripId, out index);
    }
}
