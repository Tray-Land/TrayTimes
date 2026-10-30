using TrayTimes.Transit;

namespace TrayTimes.Commute;

/// <summary>The trip view's live options and selection, computed only from its plan and shared departures.</summary>
public sealed class TripSelection
{
    public IReadOnlyList<LiveTrip> Options { get; private set; } = [];

    public LiveTrip? Selected { get; private set; }

    /// <summary>
    /// Refreshes up to three transit options and the walk-only fallback. Keeps the selected itinerary
    /// while feasible; otherwise selects the suggestion's best trip. An opening view can prefer the
    /// itinerary shown on its card.
    /// </summary>
    public void Update(TripPlan plan, IEnumerable<Arrival> arrivals, DateTimeOffset now, TripItinerary? preferred = null, TripTime time = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(arrivals);
        Arrival[] batch = [.. arrivals];
        TripItinerary? keep = preferred ?? Selected?.Itinerary;
        TripItinerary[] rides = [.. plan.Itineraries.Where(i => i.Legs.OfType<RideLeg>().Any()).Take(PlaceBoard.TripOptions)];
        TripItinerary? walk = plan.Itineraries.FirstOrDefault(i => !i.Legs.OfType<RideLeg>().Any());
        IEnumerable<LiveTrip> options = rides.Concat(walk is null ? [] : [walk])
            .Select(i => time.UsesLiveTimes(i, now)
                ? LiveTrip.Compute(i, batch, time.Mode != TripTimeMode.Now && i.Departs > now ? i.Departs : now)
                : LiveTrip.Scheduled(i))
            .OfType<LiveTrip>()
            .Where(t => time.Mode != TripTimeMode.LeaveAt || t.LeaveBy >= time.At)
            .Where(t => time.Mode != TripTimeMode.ArriveBy || t.Arrives <= time.At);
        Options = time.Mode == TripTimeMode.ArriveBy ? [.. options.OrderByDescending(t => t.LeaveBy)] : [.. options];
        Selected = Options.FirstOrDefault(t => ReferenceEquals(t.Itinerary, keep))
            ?? (time.Mode == TripTimeMode.ArriveBy ? Options.FirstOrDefault() : null)
            ?? Options.Where(t => t.Legs.OfType<LiveRide>().Any())
                .OrderBy(t => t.Arrives).ThenBy(t => t.Itinerary.Transfers).ThenByDescending(t => t.LeaveBy).FirstOrDefault()
            ?? Options.FirstOrDefault();
    }

    /// <summary>Selects one of the currently displayed options without requesting data.</summary>
    public void Select(TripItinerary itinerary)
    {
        ArgumentNullException.ThrowIfNull(itinerary);
        Selected = Options.FirstOrDefault(t => ReferenceEquals(t.Itinerary, itinerary))
            ?? throw new ArgumentException("The itinerary is not a current trip option.", nameof(itinerary));
    }
}
