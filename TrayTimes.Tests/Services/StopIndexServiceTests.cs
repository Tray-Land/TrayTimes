using System.Text.Json;
using TrayTimes.Services;
using TrayTimes.Tests.Transit;
using TrayTimes.Transit;
using Windows.Storage;

namespace TrayTimes.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class StopIndexServiceTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"TrayTimes-metadata-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        ApplicationData.Current.LocalFolder.Path = _directory;
    }

    [TestCleanup]
    public void Cleanup()
    {
        File.Delete(Path.Combine(_directory, "stop-index.json"));
        Directory.Delete(_directory);
        ApplicationData.Current.LocalFolder.Path = string.Empty;
    }

    [TestMethod]
    public async Task GetAsync_FreshCompleteDiskMetadata_PopulatesSharedCachesWithoutHttpAfterUnload()
    {
        // Arrange
        using RequestTestHandler handler = CreateHandler();
        MetadataTransitService service = new(handler.CreateService());
        StopIndexFile file = new()
        {
            BuiltAt = DateTimeOffset.Now.AddDays(-1),
            Metadata = MetadataTransitServiceTests.CompleteMetadata(),
            Stops = [new("1", "First", 43.1, -87.1, [])],
        };
        await WriteAsync(file);
        StopIndexService index = new(service);

        // Act
        await index.GetAsync();
        await service.GetRoutesAsync();
        await service.GetDirectionsAsync("14");
        IReadOnlyList<TransitStop> stops = await service.GetStopsAsync("14", "N");
        index.Unload();
        await index.GetAsync();
        await service.GetStopsAsync("14", "N");

        // Assert
        Assert.AreEqual(0, handler.Attempts);
        CollectionAssert.AreEqual(new[] { "2", "1" }, stops.Select(s => s.Id).ToArray());
        Assert.AreEqual(1, index.Count);
        Assert.AreEqual(file.BuiltAt, index.BuiltAt);
    }

    [TestMethod]
    public async Task GetAsync_LegacyDirectoryWithNoOrderedMetadata_UsesGeographyButPickerFallsBack()
    {
        // Arrange
        using RequestTestHandler handler = CreateHandler();
        MetadataTransitService service = new(handler.CreateService());
        await WriteAsync(new StopIndexFile { BuiltAt = DateTimeOffset.Now, Stops = [new("1", "First", 43.1, -87.1, [])] });
        StopIndexService index = new(service);

        // Act
        await index.GetAsync();
        int afterDirectory = handler.Attempts;
        await service.GetRoutesAsync();
        await service.GetDirectionsAsync("14");
        await service.GetStopsAsync("14", "N");

        // Assert
        Assert.AreEqual(0, afterDirectory);
        Assert.AreEqual(3, handler.Attempts);
    }

    [TestMethod]
    public async Task RebuildAsync_FreshCachesAndConcurrentManualCalls_RefreshesOnceAndPersistsCompleteMetadata()
    {
        // Arrange
        using RequestTestHandler handler = CreateHandler();
        MetadataTransitService service = new(handler.CreateService());
        await WriteAsync(new StopIndexFile { BuiltAt = DateTimeOffset.Now, Metadata = MetadataTransitServiceTests.CompleteMetadata(), Stops = [new("1", "First", 43.1, -87.1, [])] });
        StopIndexService index = new(service);
        await index.GetAsync();
        await service.GetPatternsAsync("14");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.RespondAsync = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody);
        };

        // Act
        Task first = index.RebuildAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = index.RebuildAsync();
        release.SetResult();
        await Task.WhenAll(first, second);
        StopIndexFile? persisted = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(_directory, "stop-index.json")), StopIndexJsonContext.Default.StopIndexFile);
        await service.GetStopsAsync("14", "N");
        await service.GetPatternsAsync("14");

        // Assert
        Assert.IsNotNull(persisted?.Metadata);
        Assert.HasCount(1, persisted.Metadata.Routes);
        Assert.AreEqual(5, handler.Attempts); // One old shape, routes/directions/stops rebuild, one new shape.
        Assert.IsFalse(index.IsBuilding);
    }

    [TestMethod]
    public async Task GetAsync_ExpiredDiskCache_RebuildsInsteadOfSeedingOldMetadata()
    {
        // Arrange
        using RequestTestHandler handler = CreateHandler();
        MetadataTransitService service = new(handler.CreateService());
        await WriteAsync(new StopIndexFile { BuiltAt = DateTimeOffset.Now.AddDays(-8), Metadata = MetadataTransitServiceTests.CompleteMetadata() });

        // Act
        await new StopIndexService(service).GetAsync();

        // Assert
        Assert.AreEqual(3, handler.Attempts);
    }

    private Task WriteAsync(StopIndexFile file) =>
        File.WriteAllTextAsync(Path.Combine(_directory, "stop-index.json"), JsonSerializer.Serialize(file, StopIndexJsonContext.Default.StopIndexFile));

    private static RequestTestHandler CreateHandler() => new()
    {
        RespondAsync = (_, _) => Task.FromResult(RequestTestHandler.Response(MetadataTransitServiceTests.MetadataBody)),
    };
}
