using System.Text.Json;
using System.Text.Json.Serialization;
using TrayTimes.Models;
using Windows.Storage;

namespace TrayTimes.Services;

[JsonSerializable(typeof(List<SavedStop>))]
[JsonSerializable(typeof(Place))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Typed wrapper over <see cref="ApplicationData.LocalSettings"/>. Every read falls back to a
/// default so a missing or corrupt value never crashes the app.
/// </summary>
internal static class SettingsService
{
    /// <summary>Raised after the saved stops list changes.</summary>
    public static event EventHandler? StopsChanged;

    /// <summary>Raised after the home or work place changes.</summary>
    public static event EventHandler? PlacesChanged;

    public static Place? Home
    {
        get => GetPlace(nameof(Home));
        set => SetPlace(nameof(Home), value);
    }

    public static Place? Work
    {
        get => GetPlace(nameof(Work));
        set => SetPlace(nameof(Work), value);
    }

    /// <summary>The flyout tab the user last chose.</summary>
    public static FlyoutMode FlyoutMode
    {
        get => (FlyoutMode)Get(nameof(FlyoutMode), (int)FlyoutMode.Saved);
        set => Set(nameof(FlyoutMode), (int)value);
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
        Set(nameof(Stops), JsonSerializer.Serialize(stops.ToList(), SettingsJsonContext.Default.ListSavedStop));
        StopsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Adds (or replaces, if the same stop/route/direction is already saved) a stop.</summary>
    public static void AddStop(SavedStop stop)
    {
        List<SavedStop> stops = Stops.Where(s => s.Key != stop.Key).ToList();
        stops.Add(stop);
        SetStops(stops);
    }

    public static void RemoveStop(SavedStop stop) => SetStops(Stops.Where(s => s.Key != stop.Key));

    private static Place? GetPlace(string key)
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

    private static void SetPlace(string key, Place? place)
    {
        Set(key, place is null ? null : JsonSerializer.Serialize(place, SettingsJsonContext.Default.Place));
        PlacesChanged?.Invoke(null, EventArgs.Empty);
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
