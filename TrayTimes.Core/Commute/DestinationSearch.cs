using System.Net.Http;
using System.Text.Json;
using TrayTimes.Models;
using TrayTimes.Transit;

namespace TrayTimes.Commute;

public sealed record DestinationResult(string Name, GeoPoint Location, string? StopId = null)
{
    public FavoritePlace AsPlace() => new(Guid.NewGuid().ToString("N"), PlaceKind.Custom, Name,
        new Place(Name, Location.Lat, Location.Lon), StopId);
}

public sealed record DestinationSearchResults(IReadOnlyList<DestinationResult> Items, bool AddressFailed, bool StopsFailed, bool StopsNeedLocation = false);

/// <summary>Submit-only address and stop lookup. Queries and results are never logged or persisted.</summary>
public sealed class DestinationSearch(
    Func<string, CancellationToken, Task<IReadOnlyList<GeocodeResult>>>? addresses,
    Func<GeoPoint, string, CancellationToken, Task<IReadOnlyList<TransitStop>>> stops)
{
    public const int MaxQueryLength = 200;

    /// <summary>Street addresses need only geocoding; names, intersections, and stop codes also search stops.</summary>
    public static bool IsStopQuery(string query)
    {
        string[] words = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return !query.Contains(',') && !(words.Length > 1 && words[0].Any(char.IsDigit)
            && words[0].All(c => char.IsDigit(c) || c == '-'));
    }

    public async Task<DestinationSearchResults> SearchAsync(string query, GeoPoint? near, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        query = query.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Length, MaxQueryLength);
        cancellationToken.ThrowIfCancellationRequested();
        Task<(IReadOnlyList<GeocodeResult> Items, bool Failed)> addressTask = addresses is null
            ? Task.FromResult<(IReadOnlyList<GeocodeResult>, bool)>(([], false))
            : TrySearchAsync(() => addresses(query, cancellationToken), cancellationToken);
        bool wantsStops = addresses is null || IsStopQuery(query);
        Task<(IReadOnlyList<TransitStop> Items, bool Failed)> stopTask = wantsStops && near is { } point
            ? TrySearchAsync(() => stops(point, query, cancellationToken), cancellationToken)
            : Task.FromResult<(IReadOnlyList<TransitStop>, bool)>(([], false));
        await Task.WhenAll(addressTask, stopTask).ConfigureAwait(false);
        var addressResults = await addressTask.ConfigureAwait(false);
        var stopResults = await stopTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        DestinationResult[] foundAddresses = [.. addressResults.Items.Select(a => new DestinationResult(a.Address, a.Location))];
        DestinationResult[] foundStops = [.. stopResults.Items.DistinctBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new DestinationResult(s.Name, s.Location, s.Id))];
        return new DestinationSearchResults(
            [.. foundAddresses.Where(IsValid), .. foundStops.Where(IsValid)],
            addressResults.Failed || foundAddresses.Any(r => !IsValid(r)),
            stopResults.Failed || foundStops.Any(r => !IsValid(r)),
            StopsNeedLocation: wantsStops && near is null);
    }

    private static bool IsValid(DestinationResult result) =>
        !string.IsNullOrWhiteSpace(result.Name) && (result.StopId is null || !string.IsNullOrWhiteSpace(result.StopId))
        && double.IsFinite(result.Location.Lat) && result.Location.Lat is >= -90 and <= 90
        && double.IsFinite(result.Location.Lon) && result.Location.Lon is >= -180 and <= 180;

    private static async Task<(IReadOnlyList<T> Items, bool Failed)> TrySearchAsync<T>(
        Func<Task<IReadOnlyList<T>>> search, CancellationToken cancellationToken)
    {
        try
        {
            return (await search().ConfigureAwait(false), false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or JsonException or TransitApiException)
        {
            return ([], true); // The caller displays a provider-specific failure, even if the other provider succeeded.
        }
    }
}
