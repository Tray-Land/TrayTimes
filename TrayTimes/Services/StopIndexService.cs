using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrayTimes.Models;
using TrayTimes.Transit;
using Windows.Storage;

namespace TrayTimes.Services;

internal sealed class StopIndexFile
{
    public DateTimeOffset BuiltAt { get; set; }

    public List<IndexedStop> Stops { get; set; } = [];
}

[JsonSerializable(typeof(StopIndexFile))]
internal sealed partial class StopIndexJsonContext : JsonSerializerContext;

/// <summary>
/// Every stop in the system, for "stops near me" lookups. BusTime has no geographic stop search,
/// so this crawls routes → directions → stops (about a hundred requests for MCTS), caches the
/// result in LocalState, and rebuilds it weekly.
/// </summary>
internal sealed class StopIndexService(ITransitService service)
{
    private const string FileName = "stop-index.json";
    private const int MaxConcurrentRequests = 4;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private Task<IReadOnlyList<IndexedStop>>? _loadTask;

    /// <summary>Raised (on the thread that finished the work) when the directory loads or rebuilds.</summary>
    public event EventHandler? Changed;

    public bool IsBuilding { get; private set; }

    public DateTimeOffset? BuiltAt { get; private set; }

    public int Count { get; private set; }

    /// <summary>The directory, loading it from disk or building it on first use.</summary>
    public async Task<IReadOnlyList<IndexedStop>> GetAsync()
    {
        _loadTask ??= LoadOrBuildAsync(forceRebuild: false);
        try
        {
            return await _loadTask;
        }
        catch
        {
            // Don't cache the failure; the next caller retries.
            _loadTask = null;
            throw;
        }
    }

    /// <summary>Drops the in-memory directory (it reloads from disk on next use), unless a build is running.</summary>
    public void Unload()
    {
        if (!IsBuilding)
        {
            _loadTask = null;
        }
    }

    public Task<IReadOnlyList<IndexedStop>> RebuildAsync()
    {
        _loadTask = LoadOrBuildAsync(forceRebuild: true);
        return GetAsync();
    }

    private async Task<IReadOnlyList<IndexedStop>> LoadOrBuildAsync(bool forceRebuild)
    {
        string path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
        if (!forceRebuild && await TryReadAsync(path) is { } cached && DateTimeOffset.Now - cached.BuiltAt < MaxAge)
        {
            Publish(cached);
            return cached.Stops;
        }

        IsBuilding = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            StopIndexFile built = await BuildAsync();
            await using (FileStream stream = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(stream, built, StopIndexJsonContext.Default.StopIndexFile);
            }

            Publish(built);
            return built.Stops;
        }
        finally
        {
            IsBuilding = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<StopIndexFile> BuildAsync()
    {
        IReadOnlyList<TransitRoute> routes = await service.GetRoutesAsync();
        Dictionary<string, IndexedStop> stops = [];
        using SemaphoreSlim throttle = new(MaxConcurrentRequests);
        int failures = 0;

        async Task CrawlRouteAsync(TransitRoute route)
        {
            await throttle.WaitAsync();
            try
            {
                foreach (TransitDirection direction in await service.GetDirectionsAsync(route.Id))
                {
                    StopRouteInfo info = new(route.Id, route.DisplayId, route.Color, direction.Id, direction.Name);
                    foreach (TransitStop stop in await service.GetStopsAsync(route.Id, direction.Id))
                    {
                        lock (stops)
                        {
                            if (!stops.TryGetValue(stop.Id, out IndexedStop? entry))
                            {
                                entry = new IndexedStop(stop.Id, stop.Name, stop.Location.Lat, stop.Location.Lon, []);
                                stops[stop.Id] = entry;
                            }

                            entry.Routes.Add(info);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException)
            {
                // One bad route shouldn't sink the directory; a failed build is retried next time.
                Interlocked.Increment(ref failures);
            }
            finally
            {
                throttle.Release();
            }
        }

        await Task.WhenAll(routes.Select(CrawlRouteAsync));
        if (stops.Count == 0 || failures > routes.Count / 4)
        {
            throw new TransitApiException($"Couldn't download the {service.AgencyName} stop list ({failures} of {routes.Count} routes failed).");
        }

        return new StopIndexFile { BuiltAt = DateTimeOffset.Now, Stops = [.. stops.Values] };
    }

    private static async Task<StopIndexFile?> TryReadAsync(string path)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, StopIndexJsonContext.Default.StopIndexFile);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Publish(StopIndexFile file)
    {
        BuiltAt = file.BuiltAt;
        Count = file.Stops.Count;
    }
}
