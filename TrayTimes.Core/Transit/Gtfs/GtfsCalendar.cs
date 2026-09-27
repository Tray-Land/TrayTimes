using System.Globalization;

namespace TrayTimes.Transit.Gtfs;

/// <summary>Which days each GTFS service runs, from calendar.txt and calendar_dates.txt (either may be absent).</summary>
internal sealed class GtfsCalendar
{
    // Indexed by DayOfWeek (Sunday = 0).
    private static readonly string[] DayColumns = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private readonly Dictionary<string, WeeklyService> _weekly = [];
    private readonly Dictionary<string, HashSet<DateOnly>> _added = [];
    private readonly Dictionary<string, HashSet<DateOnly>> _removed = [];

    public void ReadCalendar(GtfsCsvReader csv)
    {
        int service = csv.RequiredColumn("service_id");
        int start = csv.RequiredColumn("start_date");
        int end = csv.RequiredColumn("end_date");
        int[] days = [.. DayColumns.Select(csv.RequiredColumn)];
        while (csv.Read())
        {
            if (TryParseDate(csv[start], out DateOnly from) && TryParseDate(csv[end], out DateOnly to))
            {
                _weekly[csv[service]] = new WeeklyService([.. days.Select(d => csv[d] == "1")], from, to);
            }
        }
    }

    public void ReadCalendarDates(GtfsCsvReader csv)
    {
        int service = csv.RequiredColumn("service_id");
        int date = csv.RequiredColumn("date");
        int type = csv.RequiredColumn("exception_type");
        while (csv.Read())
        {
            if (!TryParseDate(csv[date], out DateOnly day))
            {
                continue;
            }

            // 1 adds service on that date; 2 removes it.
            Dictionary<string, HashSet<DateOnly>>? target = csv[type] switch
            {
                "1" => _added,
                "2" => _removed,
                _ => null,
            };
            if (target is not null)
            {
                string id = csv[service];
                if (!target.TryGetValue(id, out HashSet<DateOnly>? dates))
                {
                    dates = [];
                    target[id] = dates;
                }

                dates.Add(day);
            }
        }
    }

    public bool IsActive(string serviceId, DateOnly date)
    {
        if (_removed.TryGetValue(serviceId, out HashSet<DateOnly>? removed) && removed.Contains(date))
        {
            return false;
        }

        if (_added.TryGetValue(serviceId, out HashSet<DateOnly>? added) && added.Contains(date))
        {
            return true;
        }

        return _weekly.TryGetValue(serviceId, out WeeklyService? weekly)
            && date >= weekly.Start
            && date <= weekly.End
            && weekly.Days[(int)date.DayOfWeek];
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private sealed record WeeklyService(bool[] Days, DateOnly Start, DateOnly End);
}
