using System.Text.Json;
using Microsoft.UI.Dispatching;
using TrayTimes.Transit;
using Windows.Storage;

namespace TrayTimes.Services;

/// <summary>
/// Keeps the shared transit metadata (nearby lines, trip plans, route details, shapes) in
/// LocalState, so a restart doesn't repeat discovery calls. Saves a few seconds after the cache
/// changes and on exit. Call on the UI thread.
/// </summary>
internal sealed class TransitCacheService
{
    private const string FileName = "transit-cache.json";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(10);

    private readonly MetadataTransitService _metadata;
    private readonly DispatcherQueueTimer _saveTimer;
    private Task _saving = Task.CompletedTask;

    public TransitCacheService(MetadataTransitService metadata, DispatcherQueue dispatcher)
    {
        _metadata = metadata;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = SaveDelay;
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _ = SaveAsync();
        metadata.Changed += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (!_saveTimer.IsRunning)
            {
                _saveTimer.Start();
            }
        });
    }

    private static string FilePath => Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);

    /// <summary>Seeds the cache from disk; a missing or unreadable file just means starting cold.</summary>
    public async Task LoadAsync()
    {
        try
        {
            await using FileStream stream = File.OpenRead(FilePath);
            _metadata.Import(await JsonSerializer.DeserializeAsync(stream, TransitCacheJsonContext.Default.TransitCacheSnapshot));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"Transit cache not loaded: {ex.GetType().Name}.");
        }
    }

    /// <summary>Writes a snapshot now (it replaces the file only once fully written).</summary>
    public Task SaveAsync()
    {
        _saveTimer.Stop();
        TransitCacheSnapshot snapshot = _metadata.Export();
        _saving = _saving.ContinueWith(_ => WriteAsync(snapshot), TaskScheduler.Default).Unwrap();
        return _saving;
    }

    private static async Task WriteAsync(TransitCacheSnapshot snapshot)
    {
        string path = FilePath;
        string temp = path + ".tmp";
        try
        {
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, TransitCacheJsonContext.Default.TransitCacheSnapshot);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"Transit cache not saved: {ex.GetType().Name}.");
        }
    }
}
