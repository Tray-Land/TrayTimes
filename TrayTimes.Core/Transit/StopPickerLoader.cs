namespace TrayTimes.Transit;

/// <summary>Publishes stop choices independently of optional shapes, on the caller's context.</summary>
public sealed class StopPickerLoader(ITransitService service)
{
    public async Task LoadAsync(
        string routeId, string directionId,
        Action<IReadOnlyList<TransitStop>> publishStops,
        Action<IReadOnlyList<RoutePattern>> publishPatterns,
        Action<Exception> patternFailed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<TransitStop>> stopLoad = service.GetStopsAsync(routeId, directionId, lifetime.Token);
        if (stopLoad.IsCompleted && !stopLoad.IsCompletedSuccessfully)
        {
            await stopLoad;
        }

        Task patterns = LoadPatternsAsync(routeId, publishPatterns, patternFailed, lifetime.Token);
        try
        {
            IReadOnlyList<TransitStop> stops = await stopLoad;
            lifetime.Token.ThrowIfCancellationRequested();
            publishStops(stops);
        }
        catch
        {
            lifetime.Cancel();
            await patterns;
            throw;
        }

        await patterns;
    }

    private async Task LoadPatternsAsync(
        string routeId, Action<IReadOnlyList<RoutePattern>> publish, Action<Exception> failed, CancellationToken token)
    {
        try
        {
            IReadOnlyList<RoutePattern> patterns = await service.GetPatternsAsync(routeId, token);
            token.ThrowIfCancellationRequested();
            publish(patterns);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The selection/window was abandoned, or its required stop list failed.
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TransitApiException)
        {
            if (!token.IsCancellationRequested)
            {
                failed(ex);
            }
        }
    }
}
