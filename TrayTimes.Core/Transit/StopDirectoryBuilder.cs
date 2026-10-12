using System.Net;
using TrayTimes.Models;

namespace TrayTimes.Transit;

/// <summary>
/// Crawls routes → directions → stops into one stop list. The crawl stops sending requests as soon
/// as it is clear the build can't succeed: a rate limit, a rejected key, a cooldown, or too many
/// routes failing.
/// </summary>
public sealed class StopDirectoryBuilder(ITransitService service, int maxConcurrentRequests = 4)
{
    /// <summary>Complete ordered metadata from the last successful build; never inferred from a partial stop list.</summary>
    public StopDirectoryMetadata? Metadata { get; private set; }

    public async Task<IReadOnlyList<IndexedStop>> BuildAsync(CancellationToken cancellationToken = default)
    {
        Metadata = null;
        IReadOnlyList<TransitRoute> routes = await service.GetRoutesAsync(cancellationToken);
        Dictionary<string, IndexedStop> stops = [];
        Dictionary<string, DirectoryRouteMetadata> metadata = new(StringComparer.Ordinal);
        using SemaphoreSlim throttle = new(maxConcurrentRequests);
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int failures = 0;
        Exception? fatal = null;
        int failureLimit = routes.Count / 4;

        async Task CrawlRouteAsync(TransitRoute route)
        {
            try
            {
                await throttle.WaitAsync(abort.Token);
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
                return;
            }

            try
            {
                List<DirectoryDirectionMetadata> directions = [];
                foreach (TransitDirection direction in await service.GetDirectionsAsync(route.Id, abort.Token))
                {
                    StopRouteInfo info = new(route.Id, route.DisplayId, route.Color, direction.Id, direction.Name);
                    IReadOnlyList<TransitStop> orderedStops = await service.GetStopsAsync(route.Id, direction.Id, abort.Token);
                    directions.Add(new DirectoryDirectionMetadata(direction, orderedStops));
                    foreach (TransitStop stop in orderedStops)
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

                    lock (stops)
                    {
                        metadata[route.Id] = new DirectoryRouteMetadata(route, directions);
                    }
                }
            }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
                // Another route already doomed this build, or the caller gave up.
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TransitApiException)
            {
                // One bad route shouldn't sink the directory, but a build that can't succeed should stop early.
                int failed = Interlocked.Increment(ref failures);
                if (IsFatal(ex))
                {
                    Interlocked.CompareExchange(ref fatal, ex, null);
                    abort.Cancel();
                }
                else if (failed > failureLimit)
                {
                    abort.Cancel();
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        await Task.WhenAll(routes.Select(CrawlRouteAsync));
        cancellationToken.ThrowIfCancellationRequested();
        if (fatal is not null)
        {
            throw fatal;
        }

        if (stops.Count == 0 || failures > failureLimit)
        {
            throw new TransitApiException($"Couldn't download the {service.AgencyName} stop list ({failures} of {routes.Count} routes failed).");
        }

        if (failures == 0)
        {
            Metadata = new StopDirectoryMetadata(1, routes.Select(r => metadata[r.Id]).ToArray());
        }

        return [.. stops.Values];
    }

    // Retrying more routes cannot help once the service is limiting, rejecting, or on hold.
    private static bool IsFatal(Exception exception) => exception is
        TransitRateLimitException or TransitCooldownException
        or TransitApiException { Kind: not TransitApiFailureKind.Other }
        or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
}
