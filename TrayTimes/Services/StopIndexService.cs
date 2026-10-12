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

    public StopDirectoryMetadata? Metadata { get; set; }
}

[JsonSerializable(typeof(StopIndexFile))]
internal sealed partial class StopIndexJsonContext : JsonSerializerContext;

/// <summary>
/// Every stop in the system, for "stops near me" lookups. BusTime has no geographic stop search,
/// so this crawls routes → directions → stops (1 + routes + directions requests: 148 for MCTS), caches the
/// result in LocalState, and rebuilds it weekly.
/// </summary>
internal sealed class StopIndexService
{
    private const string FileName = "stop-index.json";
    private static readonly TimeSpan MaxAge = MetadataTransitService.DirectoryLifetime;

    private readonly StopDirectoryBuildCoordinator _builder;
    private readonly MetadataTransitService _metadata;
    private StopDirectoryMetadata? _builtMetadata;
    private Task<IReadOnlyList<IndexedStop>>? _loadTask;

    public StopIndexService(MetadataTransitService service)
    {
        _metadata = service;
        _builder = new StopDirectoryBuildCoordinator(async cancellationToken =>
        {
            // A rebuild must actually refresh the directory, not crawl still-fresh cached endpoints.
            _metadata.Invalidate();
            StopDirectoryBuilder builder = new(service);
            IReadOnlyList<IndexedStop> stops = await builder.BuildAsync(cancellationToken);
            _builtMetadata = builder.Metadata;
            return stops;
        });
    }

    /// <summary>Raised (on the thread that finished the work) when the directory loads or rebuilds.</summary>
    public event EventHandler? Changed;

    public bool IsBuilding { get; private set; }

    public DateTimeOffset? BuiltAt { get; private set; }

    public int Count { get; private set; }

    /// <summary>The directory, loading it from disk or building it on first use.</summary>
    public async Task<IReadOnlyList<IndexedStop>> GetAsync()
    {
        Task<IReadOnlyList<IndexedStop>> task = _loadTask ??= LoadOrBuildAsync(forceRebuild: false);
        try
        {
            return await task;
        }
        catch
        {
            // Don't cache the failure; the next caller retries (after the build hold-off). A newer load must survive.
            _ = Interlocked.CompareExchange(ref _loadTask, null, task);
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

    /// <summary>A user-requested rebuild: it ignores the failure hold-off but joins a build that is already running.</summary>
    public Task<IReadOnlyList<IndexedStop>> RebuildAsync()
    {
        if (!IsBuilding || _loadTask is null)
        {
            _loadTask = LoadOrBuildAsync(forceRebuild: true);
        }

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
            StopIndexFile built = await BuildAsync(forceRebuild);
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

    private async Task<StopIndexFile> BuildAsync(bool manual)
    {
        using IDisposable scope = TransitRequestDiagnostics.BeginScope(
            TransitConsumer.StopDirectory,
            manual ? TransitTrigger.ManualRefresh : TransitTrigger.DirectoryBuild);
        IReadOnlyList<IndexedStop> stops = await _builder.BuildAsync(manual);
        return new StopIndexFile { BuiltAt = DateTimeOffset.Now, Stops = [.. stops], Metadata = _builtMetadata };
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
        _metadata.SeedDirectory(file.Metadata, file.BuiltAt);
    }
}
