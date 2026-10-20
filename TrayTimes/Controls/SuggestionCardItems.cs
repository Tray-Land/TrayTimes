using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TrayTimes.Commute;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Views;
using Windows.UI;

namespace TrayTimes.Controls;

/// <summary>What the suggestion card shows: the suggested trip at live times, or a notice in its place.</summary>
public sealed class SuggestionCardItem
{
    private SuggestionCardItem(string key, string glyph, string title, string detail)
    {
        Key = key;
        Glyph = glyph;
        Title = title;
        Detail = detail;
    }

    public static SuggestionCardItem Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>Which trip or notice this is, apart from its times, so only a new one animates in.</summary>
    public string Key { get; }

    public string Glyph { get; }

    /// <summary>"To Work · leave in 6 min", or a notice's title.</summary>
    public string Title { get; }

    /// <summary>"arrive 8:42 · 1 transfer", or a notice's explanation.</summary>
    public string Detail { get; }

    /// <summary>A caveat under the trip, such as its having been guessed from the time of day.</summary>
    public string Note { get; private init; } = string.Empty;

    /// <summary>The trip's rides, in order.</summary>
    public IReadOnlyList<RouteChipItem> Routes { get; private init; } = [];

    public LiveTrip? Trip { get; private init; }

    public CommuteLeg? Leg { get; private init; }

    public bool IsBusy { get; private init; }

    public string AutomationName { get; private init; } = string.Empty;

    /// <summary>What selecting the trip does.</summary>
    public string TripToolTip { get; private init; } = string.Empty;

    /// <summary>The trip in one line for the tray tooltip, e.g. "Work: leave in 6 min, 14 at 8:12"; null for a notice.</summary>
    public string? TooltipText { get; private init; }

    public string? ActionText { get; private init; }

    public Action? Action { get; private init; }

    public bool CanChooseDestination { get; private init; } = true;

    public Visibility TripVisibility => Trip is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoticeVisibility => Trip is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GlyphVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActionVisibility => ActionText is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DestinationVisibility => CanChooseDestination ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Everything the card shows; equal signatures mean the card needn't be updated.</summary>
    public string Signature => string.Join('|', Key, Glyph, Title, Detail, Note, IsBusy, AutomationName, TripToolTip, ActionText, CanChooseDestination,
        string.Join(';', Routes.Select(r => r.Signature)));

    /// <summary>The trip to <paramref name="leg"/>'s destination, counting down to when to leave.</summary>
    public static SuggestionCardItem ForTrip(CommuteLeg leg, LiveTrip trip, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(trip);
        string to = leg.To.Name;
        string arrive = Clock(trip.Arrives);
        LiveRide[] rides = [.. trip.Legs.OfType<LiveRide>()];
        string title, detail, name, tooltip;
        if (rides.Length == 0)
        {
            int walk = Math.Max(1, (int)Math.Ceiling((trip.Arrives - trip.LeaveBy).TotalMinutes));
            title = AppStrings.Format("SuggestionTitleFormat", to, AppStrings.Format("SuggestionWalkFormat", walk));
            detail = AppStrings.Format("SuggestionArriveFormat", arrive);
            name = AppStrings.Format("SuggestionWalkNameFormat", to, walk, arrive);
            tooltip = AppStrings.Format("TooltipWalkFormat", to, walk, arrive);
        }
        else
        {
            int leaveIn = Math.Max(0, (int)Math.Floor((trip.LeaveBy - now).TotalMinutes));
            string leave = leaveIn == 0 ? AppStrings.Get("SuggestionLeaveNow") : AppStrings.Format("SuggestionLeaveInFormat", leaveIn);
            string spokenLeave = leaveIn switch
            {
                0 => AppStrings.Get("SuggestionLeaveNow"),
                1 => AppStrings.Get("SuggestionLeaveInOneMinute"),
                _ => AppStrings.Format("SuggestionLeaveInMinutesFormat", leaveIn),
            };
            int transfers = trip.Itinerary.Transfers;
            string changes = transfers == 1 ? AppStrings.Get("SuggestionOneTransfer") : AppStrings.Format("SuggestionTransfersFormat", transfers);
            title = AppStrings.Format("SuggestionTitleFormat", to, leave);
            detail = transfers == 0 ? AppStrings.Format("SuggestionArriveFormat", arrive) : AppStrings.Format("SuggestionArriveTransfersFormat", arrive, changes);
            name = AppStrings.Format("SuggestionTripNameFormat", to, spokenLeave, arrive, transfers == 0 ? AppStrings.Get("SuggestionNoTransfers") : changes);
            tooltip = AppStrings.Format("TooltipTripFormat", to, leave, rides[0].Line.Route.DisplayId, Clock(rides[0].Starts));
        }

        string note = leg.IsGuess && leg.From is { } from ? AppStrings.Format("SuggestionGuessedFormat", from.Name) : string.Empty;
        return new SuggestionCardItem($"trip|{leg.To.Id}", MapHelpers.PlaceGlyph(leg.To.Kind), title, detail)
        {
            Note = note,
            Routes = [.. rides.Select((r, i) => new RouteChipItem(
                r.Line.Route.DisplayId,
                MapHelpers.ParseRouteColor(r.Departure?.RouteColor ?? r.Line.Route.Color),
                isTransfer: i > 0))],
            Trip = trip,
            Leg = leg,
            AutomationName = note.Length == 0 ? name : AppStrings.Format("SuggestionNameWithNoteFormat", name, note),
            TripToolTip = AppStrings.Get("SuggestionTripTooltip"),
            TooltipText = tooltip,
        };
    }

    /// <summary>
    /// The trip the rider started, in the suggestion's place: "Trip to Work · get off in 9 min". The tray tooltip
    /// already leads with the trip, so this adds no line to it.
    /// </summary>
    /// <param name="step">Where the rider is; null for a trip waiting to be resumed.</param>
    public static SuggestionCardItem ForActiveTrip(ActiveTrip trip, TripStep? step, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trip);
        LiveTrip live = trip.Progress.Trip;
        return new SuggestionCardItem($"active|{trip.Leg.To.Id}", MapHelpers.PlaceGlyph(trip.Leg.To.Kind), TripMessages.CardTitle(trip, step, now),
            AppStrings.Format("SuggestionArriveFormat", TripMessages.When(live.Arrives, now)))
        {
            Routes = [.. live.Legs.OfType<LiveRide>().Select((r, i) => new RouteChipItem(
                r.Line.Route.DisplayId,
                MapHelpers.ParseRouteColor(r.Departure?.RouteColor ?? r.Line.Route.Color),
                isTransfer: i > 0))],
            Trip = live,
            Leg = trip.Leg,
            AutomationName = TripMessages.CardName(trip, step, now),
            TripToolTip = AppStrings.Get("TripCardTooltip"),
            CanChooseDestination = false, // One trip at a time: end this one to plan another.
        };
    }

    /// <summary>A notice in the trip's place, such as a prompt to set up places, with its action as a button.</summary>
    /// <param name="isBusy">Shows a progress ring in place of the glyph, while a trip is being planned.</param>
    public static SuggestionCardItem ForNotice(PlaceNotice notice, string glyph, bool isBusy = false, bool canChooseDestination = true)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return new SuggestionCardItem($"notice|{notice.Title}", glyph, notice.Title, notice.Text)
        {
            IsBusy = isBusy,
            ActionText = notice.Button,
            Action = notice.Action,
            CanChooseDestination = canChooseDestination,
        };
    }

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
}

/// <summary>One ride's route badge on the suggestion card, after an arrow from the ride before it.</summary>
public sealed class RouteChipItem(string label, Color color, bool isTransfer)
{
    public string Label { get; } = label;

    public SolidColorBrush Background { get; } = new(color);

    public SolidColorBrush Foreground { get; } = new(MapHelpers.ContrastingText(color));

    public Visibility ArrowVisibility { get; } = isTransfer ? Visibility.Visible : Visibility.Collapsed;

    public string Signature => $"{Label}|{Background.Color}|{ArrowVisibility}";
}
