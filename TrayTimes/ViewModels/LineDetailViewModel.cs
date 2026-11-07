using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Services;
using TrayTimes.Transit;
using TrayTimes.Views;
using Windows.UI;

namespace TrayTimes.ViewModels;

/// <summary>The line a rider opened from a route's number on the board, and the direction that row was going.</summary>
internal sealed record LineRequest(string RouteId, string DisplayId, string? Mode, string? Color, VehicleKind VehicleKind, string DirectionId)
{
    public string Label => FlyoutViewModel.LineLabel(Mode, DisplayId);
}

/// <summary>What the map draws for the line's shown direction.</summary>
internal sealed record LineMap(RouteDetails Details, string DirectionId, IReadOnlyList<TransitStop> Stops);

/// <summary>A small, untappable badge for a route that crosses a stop.</summary>
internal sealed record TransferChip(string Label, Color Background, Color Foreground);

/// <summary>One stop in the line's list: where it is on the line, which routes cross it, and whether a bus is near it.</summary>
internal sealed partial class LineStopItem : ObservableObject
{
    private const int MaxTransferChips = 6;

    private readonly int _index;
    private readonly int _count;
    private IReadOnlyList<StopTransfer> _transfers = [];

    public LineStopItem(TransitStop stop, int index, int count, Color lineColor)
    {
        Stop = stop;
        _index = index;
        _count = count;
        LineColor = lineColor;
        LineTextColor = MapHelpers.ContrastingText(lineColor);
    }

    public TransitStop Stop { get; }

    public string Name => Stop.Name;

    public Color LineColor { get; }

    public Color LineTextColor { get; }

    public bool ShowsLineAbove => _index > 0;

    public bool ShowsLineBelow => _index < _count - 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransfers), nameof(AutomationName))]
    public partial IReadOnlyList<TransferChip> Transfers { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBus), nameof(ShowsDot), nameof(ShowsBusCount), nameof(BusText), nameof(AutomationName))]
    public partial int Vehicles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool IsFavorite { get; set; }

    public bool HasTransfers => Transfers.Count > 0;

    public bool HasBus => Vehicles > 0;

    public bool ShowsDot => Vehicles == 0;

    public bool ShowsBusCount => Vehicles > 1;

    /// <summary>The count shown on the bus marker; nothing for a single bus.</summary>
    public string BusText => Vehicles > 1 ? Vehicles.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    public string AutomationName
    {
        get
        {
            List<string> parts = [Name];
            if (_transfers.Count > 0)
            {
                parts.Add(AppStrings.Format("LineStopTransfersFormat", string.Join(", ", _transfers.Select(t => t.DisplayId))));
            }

            if (Vehicles > 0)
            {
                parts.Add(Vehicles == 1 ? AppStrings.Get("LineStopBusNearby") : AppStrings.Format("LineStopBusesNearbyFormat", Vehicles));
            }

            if (IsFavorite)
            {
                parts.Add(AppStrings.Get("LineStopFavorite"));
            }

            return string.Join(". ", parts);
        }
    }

    public void SetTransfers(IReadOnlyList<StopTransfer> transfers)
    {
        _transfers = transfers;
        List<TransferChip> chips = [.. transfers.Take(MaxTransferChips).Select(t =>
        {
            Color color = MapHelpers.ParseRouteColor(t.Color);
            return new TransferChip(RouteNames.Short(t.DisplayId), color, MapHelpers.ContrastingText(color));
        })];
        if (transfers.Count > MaxTransferChips)
        {
            Color gray = Color.FromArgb(255, 110, 110, 110);
            chips.Add(new TransferChip($"+{transfers.Count - MaxTransferChips}", gray, MapHelpers.ContrastingText(gray)));
        }

        Transfers = chips;
    }
}

/// <summary>
/// One line in detail: its route on the map, a direction to pick, and that direction's stops in order, each showing the
/// routes that cross it and the buses nearest it. It loads the line's route details once (the metadata cache keeps them), and
/// the routes crossing each stop in one batched departures call; live vehicle positions come from the flyout's own
/// vehicle monitor, which hands them to <see cref="SetVehicles"/>.
/// </summary>
internal sealed partial class LineDetailViewModel : ObservableObject
{
    private const int MaxDepartureRequests = 2; // a line of more stops than this many requests cover shows no transfers past them

    private readonly Dictionary<string, IReadOnlyList<Arrival>> _departures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase); // directions whose crossing routes were asked for
    private ITransitService? _service;
    private CancellationTokenSource? _lifetime;
    private LineRequest? _line;
    private RouteDetails? _details;
    private string _directionId = string.Empty;
    private IReadOnlyList<Vehicle> _vehicles = [];
    private Color _lineColor;

    public event EventHandler? BackRequested;

    /// <summary>Raised when the line, or the direction shown, changed, so the map can draw it.</summary>
    public event EventHandler<LineMap>? MapChanged;

    /// <summary>Raised when the directions to pick from are known (or cleared).</summary>
    public event EventHandler? DirectionsChanged;

    public event EventHandler<TransitStop>? StopChosen;

    public event EventHandler<TransitStop>? TripRequested;

    public event EventHandler<TransitStop>? FavoriteRequested;

    public ObservableCollection<LineStopItem> Stops { get; } = [];

    public TransitDirection[] Directions { get; private set; } = [];

    public string DirectionId => _directionId;

    [ObservableProperty]
    public partial string RouteLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Color RouteColor { get; set; }

    [ObservableProperty]
    public partial Color RouteTextColor { get; set; }

    [ObservableProperty]
    public partial string Heading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subheading { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool HasStatus => Status.Length > 0;

    /// <summary>Shows <paramref name="line"/>, loading what it needs from <paramref name="service"/>.</summary>
    public void Open(ITransitService service, LineRequest line)
    {
        Cancel();
        _service = service;
        _line = line;
        _details = null;
        _directionId = line.DirectionId;
        _vehicles = [];
        _departures.Clear();
        _requested.Clear();
        Stops.Clear();
        Directions = [];
        DirectionsChanged?.Invoke(this, EventArgs.Empty);
        _lineColor = MapHelpers.ParseRouteColor(line.Color);
        RouteColor = _lineColor;
        RouteTextColor = MapHelpers.ContrastingText(_lineColor);
        RouteLabel = line.DisplayId;
        Heading = line.Label;
        Subheading = string.Empty;
        _lifetime = new CancellationTokenSource();
        _ = LoadAsync(_lifetime.Token);
    }

    /// <summary>Stops loading (the page closed or the flyout hid).</summary>
    public void Cancel()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        IsLoading = false;
    }

    /// <summary>The line's vehicles moved: marks the stop each is nearest to.</summary>
    public void SetVehicles(IReadOnlyList<Vehicle> vehicles)
    {
        _vehicles = vehicles;
        ApplyVehicles();
    }

    /// <summary>Re-reads which listed stops are favorites.</summary>
    public void RefreshFavorites()
    {
        HashSet<string> saved = [.. SettingsService.Stops.Select(s => s.StopId)];
        foreach (LineStopItem item in Stops)
        {
            item.IsFavorite = saved.Contains(item.Stop.Id);
        }
    }

    /// <summary>The rider picked a direction.</summary>
    public void ChooseDirection(string directionId)
    {
        if (_details is null || string.Equals(directionId, _directionId, StringComparison.OrdinalIgnoreCase)
            || !Directions.Any(d => d.Id == directionId))
        {
            return;
        }

        ShowDirection(directionId);
        if (_lifetime is { } lifetime)
        {
            _ = LoadTransfersAsync(_directionId, lifetime.Token);
        }
    }

    public void ChooseStop(LineStopItem? item)
    {
        if (item is not null)
        {
            StopChosen?.Invoke(this, item.Stop);
        }
    }

    public void RequestTrip(TransitStop stop) => TripRequested?.Invoke(this, stop);

    public void RequestFavorite(TransitStop stop) => FavoriteRequested?.Invoke(this, stop);

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_service is not { } service || _line is not { } line)
        {
            return;
        }

        if (!service.IsConfigured)
        {
            Status = AppStrings.Get("LineNotConfigured");
            return;
        }

        IsLoading = true;
        Status = AppStrings.Get("LineLoading");
        try
        {
            using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Nearby, TransitTrigger.VehicleSelected);
            RouteDetails details = await service.GetRouteDetailsAsync(line.RouteId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _details = details;
            if (details.Route.Name.Length > 0 && !string.Equals(details.Route.Name, details.Route.DisplayId, StringComparison.OrdinalIgnoreCase))
            {
                Heading = details.Route.Name;
            }

            Directions = [.. details.Itineraries.Select(i => i.Direction).DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase)];
            if (Directions.Length == 0)
            {
                Status = AppStrings.Get("LineNoStops");
                DirectionsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            string initial = Directions.FirstOrDefault(d => string.Equals(d.Id, line.DirectionId, StringComparison.OrdinalIgnoreCase))?.Id ?? Directions[0].Id;
            Status = string.Empty;
            ShowDirection(initial);
            await LoadTransfersAsync(initial, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page closed.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or TransitApiException)
        {
            System.Diagnostics.Debug.WriteLine($"Line details failed: {ex.GetType().Name}.");
            if (_details is null)
            {
                Status = AppStrings.Get("LineLoadFailed");
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void ShowDirection(string directionId)
    {
        if (_details is not { } details)
        {
            return;
        }

        RouteItinerary? itinerary = details.Itineraries.FirstOrDefault(i => string.Equals(i.Direction.Id, directionId, StringComparison.OrdinalIgnoreCase));
        if (itinerary is null)
        {
            return;
        }

        _directionId = itinerary.Direction.Id;
        Subheading = itinerary.Direction.Name;
        _vehicles = []; // The other direction's buses aren't this one's; its own arrive with the next positions.
        Stops.Clear();
        IReadOnlyList<TransitStop> stops = itinerary.Stops;
        for (int i = 0; i < stops.Count; i++)
        {
            Stops.Add(new LineStopItem(stops[i], i, stops.Count, _lineColor));
        }

        ApplyTransfers(_directionId);
        ApplyVehicles();
        RefreshFavorites();
        DirectionsChanged?.Invoke(this, EventArgs.Empty);
        MapChanged?.Invoke(this, new LineMap(details, _directionId, stops));
        Status = stops.Count == 0 ? AppStrings.Get("LineNoStops") : string.Empty;
    }

    /// <summary>One batched departures request (or two for a very long line) for the routes that cross this direction's stops.</summary>
    private async Task LoadTransfersAsync(string directionId, CancellationToken cancellationToken)
    {
        if (_service is not { } service || _details is null || !_requested.Add(directionId))
        {
            return;
        }

        RouteItinerary? itinerary = _details.Itineraries.FirstOrDefault(i => string.Equals(i.Direction.Id, directionId, StringComparison.OrdinalIgnoreCase));
        if (itinerary is null || itinerary.Stops.Count == 0)
        {
            return;
        }

        bool loaded = false;
        try
        {
            using IDisposable scope = TransitRequestDiagnostics.BeginScope(TransitConsumer.Nearby, TransitTrigger.VehicleSelected);
            List<Arrival> arrivals = [];
            string[] ids = [.. itinerary.Stops.Select(s => s.Id).Distinct(StringComparer.Ordinal)];
            foreach (string[] batch in ids.Chunk(Math.Max(1, service.MaxStopsPerRequest)).Take(MaxDepartureRequests))
            {
                DepartureBoard board = await service.GetDeparturesAsync(batch, includeShapes: false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                arrivals.AddRange(board.Arrivals);
            }

            _departures[directionId] = arrivals;
            loaded = true;
            if (string.Equals(directionId, _directionId, StringComparison.OrdinalIgnoreCase))
            {
                ApplyTransfers(directionId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page closed or the direction changed.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or TransitApiException)
        {
            // The stops are still listed; they just show no crossing routes.
            System.Diagnostics.Debug.WriteLine($"Line transfers failed: {ex.GetType().Name}.");
        }
        finally
        {
            if (!loaded)
            {
                _requested.Remove(directionId); // Choosing the direction again tries again.
            }
        }
    }

    private void ApplyTransfers(string directionId)
    {
        if (_line is not { } line || !_departures.TryGetValue(directionId, out IReadOnlyList<Arrival>? arrivals))
        {
            return;
        }

        IReadOnlyList<LineStop> built = LineStops.Build(line.RouteId, Stops.Select(s => s.Stop).ToArray(), arrivals, []);
        for (int i = 0; i < Stops.Count && i < built.Count; i++)
        {
            Stops[i].SetTransfers(built[i].Transfers);
        }
    }

    private void ApplyVehicles()
    {
        int[] nearest = LineStops.NearestStops(Stops.Select(s => s.Stop).ToArray(), _vehicles);
        for (int i = 0; i < Stops.Count; i++)
        {
            int index = i;
            Stops[i].Vehicles = nearest.Count(n => n == index);
        }
    }
}
