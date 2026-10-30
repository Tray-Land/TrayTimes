using System.Text.Json;
using System.Text.Json.Serialization;
using TrayTimes.Models;
using TrayTimes.Transit;
using Windows.Storage;

namespace TrayTimes.Services;

[JsonSerializable(typeof(List<SavedStop>))]
[JsonSerializable(typeof(Place))]
[JsonSerializable(typeof(List<FavoritePlace>))]
[JsonSerializable(typeof(MapView))]
[JsonSerializable(typeof(ApiCallLog))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Typed wrapper over <see cref="ApplicationData.LocalSettings"/>. Every read falls back to a
/// default so a missing or corrupt value never crashes the app.
/// </summary>
internal static class SettingsService
{
    public const int MinHeadOutMinutes = 1;

    public const int MaxHeadOutMinutes = 30;

    /// <summary>The most favorite stops; they all share one departures request.</summary>
    public const int MaxFavoriteStops = 20;

    /// <summary>Saved data written by this version: 2 is the first using Transit IDs.</summary>
    public const int CurrentDataVersion = 2;

    /// <summary>Raised after the saved stops list changes.</summary>
    public static event EventHandler? StopsChanged;

    /// <summary>Raised after a favorite place is added, changed, or removed.</summary>
    public static event EventHandler? PlacesChanged;

    /// <summary>Favorite places: the Home, Work, and School slots that are set, then custom places.</summary>
    public static IReadOnlyList<FavoritePlace> Places
    {
        get
        {
            if (Get<string?>(nameof(Places), null) is not { } json)
            {
                return MigrateLegacyPlaces();
            }

            try
            {
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ListFavoritePlace) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Where the flyout's map was when it was last dismissed, so reopening picks up there.</summary>
    public static MapView? FlyoutMapView
    {
        get
        {
            if (Get<string?>(nameof(FlyoutMapView), null) is not { } json)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.MapView);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        set => Set(nameof(FlyoutMapView), value is null ? null : JsonSerializer.Serialize(value, SettingsJsonContext.Default.MapView));
    }

    /// <summary>The flyout's view the rider last picked (see <c>BoardCenter.ToKey</c>); opens there next time.</summary>
    public static string? BoardCenterKey
    {
        get => Get<string?>(nameof(BoardCenterKey), null);
        set => Set(nameof(BoardCenterKey), value);
    }

    /// <summary>The area the rider chose by postal code, where Settings' stop map opens when their location isn't available.</summary>
    public static MapView? DefaultArea
    {
        get
        {
            if (Get<string?>(nameof(DefaultArea), null) is not { } json)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.MapView);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        set => Set(nameof(DefaultArea), value is null ? null : JsonSerializer.Serialize(value, SettingsJsonContext.Default.MapView));
    }

    /// <summary>How tall the user dragged the flyout's map, in DIPs; null keeps the default.</summary>
    public static double? FlyoutMapHeight
    {
        get => Get<double?>(nameof(FlyoutMapHeight), null);
        set => Set(nameof(FlyoutMapHeight), value);
    }

    /// <summary>How many minutes before a tracked bus arrives to tell the rider to head out.</summary>
    public static int HeadOutMinutes
    {
        get => Math.Clamp(Get(nameof(HeadOutMinutes), 5), MinHeadOutMinutes, MaxHeadOutMinutes);
        set => Set(nameof(HeadOutMinutes), Math.Clamp(value, MinHeadOutMinutes, MaxHeadOutMinutes));
    }

    /// <summary>Stop the PC from sleeping while a bus is tracked, so alerts still arrive.</summary>
    public static bool KeepAwakeWhileTracking
    {
        get => Get(nameof(KeepAwakeWhileTracking), false);
        set => Set(nameof(KeepAwakeWhileTracking), value);
    }

    /// <summary>The version of the saved data; 1 (or missing) holds MCTS BusTime stop IDs.</summary>
    public static int DataVersion
    {
        get => Get(nameof(DataVersion), 1);
        set => Set(nameof(DataVersion), value);
    }

    /// <summary>The Transit API key's limits; Transit's free tier unless the developer changed them.</summary>
    public static TransitBudgetPolicy TransitBudget
    {
        get => new()
        {
            CallsPerMinute = Math.Clamp(Get("TransitCallsPerMinute", TransitBudgetPolicy.DefaultCallsPerMinute), 1, 10_000),
            CallsPerMonth = Math.Clamp(Get("TransitCallsPerMonth", TransitBudgetPolicy.DefaultCallsPerMonth), 1, 100_000_000),
        };

        set
        {
            Set("TransitCallsPerMinute", value.CallsPerMinute);
            Set("TransitCallsPerMonth", value.CallsPerMonth);
        }
    }

    /// <summary>Transit API call tally; a fresh one if none is saved or it can't be read.</summary>
    public static ApiCallLog ApiCallLog
    {
        get
        {
            if (Get<string?>(nameof(ApiCallLog), null) is not { } json)
            {
                return new ApiCallLog();
            }

            try
            {
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ApiCallLog) ?? new ApiCallLog();
            }
            catch (JsonException)
            {
                return new ApiCallLog();
            }
        }

        set => Set(nameof(ApiCallLog), JsonSerializer.Serialize(value, SettingsJsonContext.Default.ApiCallLog));
    }

    public static IReadOnlyList<SavedStop> Stops
    {
        get
        {
            string? json = Get<string?>(nameof(Stops), null);
            if (json is null)
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ListSavedStop) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public static void SetStops(IEnumerable<SavedStop> stops)
    {
        Set(nameof(Stops), JsonSerializer.Serialize([.. stops], SettingsJsonContext.Default.ListSavedStop));
        StopsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Adds (or replaces, if the same stop/route/direction is already saved) a stop.</summary>
    public static void AddStop(SavedStop stop)
    {
        IReadOnlyList<SavedStop> current = Stops;
        string? nickname = stop.Nickname ?? current.FirstOrDefault(s => s.StopId == stop.StopId && s.Nickname is not null)?.Nickname;
        List<SavedStop> stops = [.. current.Where(s => s.Key != stop.Key)];
        stops.Add(stop with { Nickname = nickname });
        SetStops(stops);
    }

    /// <summary>Names (or, when blank, un-names) a saved stop; the nickname covers every route saved there.</summary>
    public static void SetStopNickname(string stopId, string? nickname) => SetStops(SavedStop.WithNickname(Stops, stopId, nickname));

    public static void RemoveStop(SavedStop stop) => SetStops(Stops.Where(s => s.Key != stop.Key));

    /// <summary>Adds a favorite place, or replaces the one with the same ID.</summary>
    public static void SavePlace(FavoritePlace place)
    {
        List<FavoritePlace> places = [.. Places];
        int index = places.FindIndex(p => p.Id == place.Id);
        if (index >= 0)
        {
            places[index] = place;
        }
        else
        {
            places.Add(place);
        }

        SetPlaces(places);
    }

    public static void RemovePlace(string id) => SetPlaces(Places.Where(p => p.Id != id));

    private static void SetPlaces(IEnumerable<FavoritePlace> places)
    {
        Set(nameof(Places), JsonSerializer.Serialize([.. places], SettingsJsonContext.Default.ListFavoritePlace));
        PlacesChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Moves the separate Home and Work settings from earlier versions into <see cref="Places"/>.</summary>
    private static List<FavoritePlace> MigrateLegacyPlaces()
    {
        List<FavoritePlace> places = [];
        foreach (PlaceKind kind in (PlaceKind[])[PlaceKind.Home, PlaceKind.Work])
        {
            if (GetLegacyPlace(kind.ToString()) is { } place)
            {
                places.Add(FavoritePlace.ForKind(kind, place));
            }

            Set(kind.ToString(), null);
        }

        Set(nameof(Places), JsonSerializer.Serialize(places, SettingsJsonContext.Default.ListFavoritePlace));
        return places;
    }

    private static Place? GetLegacyPlace(string key)
    {
        if (Get<string?>(key, null) is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Place);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T Get<T>(string key, T defaultValue)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void Set(string key, object? value)
    {
        try
        {
            if (value is null)
            {
                ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
        }
        catch
        {
            // Settings are best-effort; ignore storage failures.
        }
    }
}
