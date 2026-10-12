using System.IO.Compression;
using System.Text;
using TrayTimes.Models;
using TrayTimes.Transit.Gtfs;

namespace TrayTimes.Tests.Services;

/// <summary>A small real GTFS feed; only the platform loader/Loaded event boundary is doubled.</summary>
internal static class ScheduleTestSupport
{
    public static GtfsTimetable Load()
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "agency.txt", "agency_timezone\nEtc/UTC");
            Write(zip, "routes.txt", "route_id,route_short_name\n14,14");
            Write(zip, "calendar_dates.txt", "service_id,date,exception_type\nDAY,20261006,1");
            Write(zip, "trips.txt", """
                route_id,service_id,trip_id,trip_headsign,direction_id
                14,DAY,first,DOWNTOWN,0
                14,DAY,later,DOWNTOWN,0
                """);
            Write(zip, "stop_times.txt", """
                trip_id,arrival_time,departure_time,stop_id,stop_sequence
                first,20:20:00,20:20:00,1,1
                first,20:25:00,20:25:00,2,2
                first,20:40:00,20:40:00,destination,3
                later,21:00:00,21:00:00,1,1
                later,21:05:00,21:05:00,2,2
                later,21:20:00,21:20:00,destination,3
                """);
        }

        stream.Position = 0;
        return GtfsTimetable.Load(stream, [.. new[] { "1", "2", "destination" }.Select(id =>
            new IndexedStop(id, "Fixture stop", 0, 0, [new StopRouteInfo("14", "14", null, "N", "North")]))]);
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
