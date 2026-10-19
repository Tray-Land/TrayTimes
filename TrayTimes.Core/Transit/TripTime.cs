using System.Text.Json.Serialization;

namespace TrayTimes.Transit;

public enum TripTimeMode { Now, LeaveAt, ArriveBy }

/// <summary>A time constraint normalized to five minutes, without moving leave-at earlier or arrive-by later.</summary>
public readonly record struct TripTime
{
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(30);

    [JsonConstructor]
    public TripTime(TripTimeMode mode, DateTimeOffset at = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Mode = mode;
        long interval = TimeSpan.FromMinutes(5).Ticks;
        long ticks = at.UtcTicks;
        long rounded = ticks - (ticks % interval);
        if (mode == TripTimeMode.LeaveAt && ticks % interval != 0)
        {
            rounded += interval;
        }

        At = mode == TripTimeMode.Now ? default : new DateTimeOffset(rounded, TimeSpan.Zero);
    }

    public TripTimeMode Mode { get; }

    public DateTimeOffset At { get; }

    /// <summary>Converts a local date and clock time; nonexistent or ambiguous daylight-saving times need another choice.</summary>
    public static TripTime FromLocal(TripTimeMode mode, DateTime date, TimeSpan time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(time, TimeSpan.FromDays(1));
        DateTime local = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new ArgumentException("Choose an unambiguous local time outside the daylight-saving transition.", nameof(time));
        }

        return new TripTime(mode, new DateTimeOffset(local, zone.GetUtcOffset(local)));
    }

    public bool UsesLiveTimes(TripItinerary itinerary, DateTimeOffset now) =>
        Mode == TripTimeMode.Now || itinerary.Departs - now <= LiveWindow;
}
