using System.Net;
using TrayTimes.Transit;

namespace TrayTimes.Tests.Transit;

[TestClass]
public sealed class StopPickerLoaderTests
{
    [TestMethod]
    public async Task LoadAsync_SlowPatterns_PublishesStopsAndEndsTheirLoadingBeforeShapes()
    {
        // Arrange
        using MetadataTransitServiceTests.Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> patterns = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("getpatterns", StringComparison.Ordinal)
            ? patterns.Task : Task.FromResult(RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody));
        StopPickerLoader loader = new(f.Service);
        TaskCompletionSource<IReadOnlyList<TransitStop>> stopsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<RoutePattern>? lines = null;
        bool stopLoading = true;

        // Act
        Task load = loader.LoadAsync("14", "N", stops => { stopLoading = false; stopsReady.SetResult(stops); },
            shapes => lines = shapes, _ => Assert.Fail("Patterns should succeed."));
        IReadOnlyList<TransitStop> choices = await stopsReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool loadStillPending = !load.IsCompleted;
        bool noLinesYet = lines is null;
        patterns.SetResult(RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody));
        await load;

        // Assert
        Assert.IsFalse(stopLoading);
        Assert.HasCount(2, choices);
        Assert.IsTrue(loadStillPending);
        Assert.IsTrue(noLinesYet);
        Assert.IsNotNull(lines);
        Assert.HasCount(1, lines);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task LoadAsync_PatternFailure_ReportsItWithoutBlockingOrClearingStops()
    {
        // Arrange
        using MetadataTransitServiceTests.Fixture f = new();
        f.Handler.RespondAsync = (request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("getpatterns", StringComparison.Ordinal)
                ? RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable)
                : RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody));
        IReadOnlyList<TransitStop>? choices = null;
        Exception? reported = null;
        bool drew = false;

        // Act
        await new StopPickerLoader(f.Service).LoadAsync("14", "N", stops => choices = stops, _ => drew = true, ex => reported = ex);

        // Assert
        Assert.IsNotNull(choices);
        Assert.HasCount(2, choices);
        Assert.IsInstanceOfType<HttpRequestException>(reported);
        Assert.IsFalse(drew);
    }

    [TestMethod]
    public async Task LoadAsync_SupersededSelection_NoOldStopsOrShapesPublishAndNewSelectionRecovers()
    {
        // Arrange
        using MetadataTransitServiceTests.Fixture f = new();
        using CancellationTokenSource oldSelection = new();
        TaskCompletionSource<HttpResponseMessage> oldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.RespondAsync = (_, _) => oldResponse.Task; // Deliberately completes despite cancellation.
        int oldPublications = 0;
        StopPickerLoader loader = new(f.Service);
        Task old = loader.LoadAsync("14", "N", _ => oldPublications++, _ => oldPublications++, _ => oldPublications++, oldSelection.Token);

        // Act
        oldSelection.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => old);
        f.Handler.RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody));
        int newPublications = 0;
        await loader.LoadAsync("14", "S", _ => newPublications++, _ => newPublications++, _ => Assert.Fail());
        oldResponse.SetResult(RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody));

        // Assert
        Assert.AreEqual(0, oldPublications);
        Assert.AreEqual(2, newPublications);
        Assert.AreEqual(4, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task LoadAsync_RequiredStopsFail_CancelsOnlyPickerShapeWaiterAndRethrows()
    {
        // Arrange
        using MetadataTransitServiceTests.Fixture f = new();
        TaskCompletionSource<HttpResponseMessage> stops = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> patterns = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken physicalPattern = default;
        f.Handler.RespondAsync = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getpatterns", StringComparison.Ordinal))
            {
                physicalPattern = token;
                return patterns.Task;
            }

            return stops.Task;
        };
        Task<IReadOnlyList<RoutePattern>> flyout = f.Service.GetPatternsAsync("14");
        int publications = 0;
        Task picker = new StopPickerLoader(f.Service).LoadAsync("14", "N", _ => publications++, _ => publications++, _ => publications++);

        // Act
        stops.SetResult(RequestTestHandler.Response("down", HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => picker);
        bool physicalCanceled = physicalPattern.IsCancellationRequested;
        patterns.SetResult(RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody));
        await flyout;

        // Assert
        Assert.IsFalse(physicalCanceled);
        Assert.AreEqual(0, publications);
        Assert.AreEqual(2, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task LoadAsync_AlreadyCanceled_DoesNotStartAnyStage()
    {
        // Arrange
        using MetadataTransitServiceTests.Fixture f = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new StopPickerLoader(f.Service).LoadAsync("14", "N", _ => Assert.Fail(), _ => Assert.Fail(), _ => Assert.Fail(), cancellation.Token));
        Assert.AreEqual(0, f.Handler.Attempts);
    }

    [TestMethod]
    public async Task LoadAsync_RecreatedPicker_ReusesStopsAndPatternsAndPreservesIndependentDirection()
    {
        // Arrange
        using MetadataTransitServiceTests.Fixture f = new();
        await new StopPickerLoader(f.Service).LoadAsync("14", "N", _ => { }, _ => { }, _ => Assert.Fail());

        // Act
        await new StopPickerLoader(f.Service).LoadAsync("14", "N", _ => { }, _ => { }, _ => Assert.Fail());
        int afterRecreation = f.Handler.Attempts;
        await new StopPickerLoader(f.Service).LoadAsync("14", "S", _ => { }, _ => { }, _ => Assert.Fail());

        // Assert
        Assert.AreEqual(2, afterRecreation);
        Assert.AreEqual(3, f.Handler.Attempts); // Only the new direction's stops.
    }
}
