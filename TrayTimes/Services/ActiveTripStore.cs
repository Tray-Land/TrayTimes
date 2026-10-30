using System.Text.Json;
using TrayTimes.Commute;

namespace TrayTimes.Services;

/// <summary>
/// Keeps the active trip in one file (LocalState\active-trip.json in the app) so a restart can offer to
/// resume it. Saves and deletes run in order, off the UI thread, and a save replaces the file only once
/// fully written. The trip names the rider's places, so its contents are never logged. Call on the UI thread.
/// </summary>
internal sealed class ActiveTripStore(string folder)
{
    public const string FileName = "active-trip.json";

    private readonly string _path = Path.Combine(folder, FileName);
    private readonly string _tempPath = Path.Combine(folder, FileName + ".tmp");
    private Task _writing = Task.CompletedTask;

    /// <summary>
    /// The saved trip; null when there's none, or when it can't be read or isn't whole (that file is deleted, as
    /// is a save that never finished). Never throws.
    /// </summary>
    public async Task<ActiveTrip?> LoadAsync()
    {
        await _writing;
        (ActiveTrip? trip, bool damaged, bool unfinished) = await Task.Run(() =>
        {
            (ActiveTrip? read, bool unusable) = Read();
            return (read, unusable, File.Exists(_tempPath));
        });
        if (damaged || unfinished)
        {
            await Enqueue(() =>
            {
                File.Delete(_tempPath);
                if (damaged)
                {
                    File.Delete(_path);
                }
            });
        }

        return trip;
    }

    public Task SaveAsync(ActiveTrip trip)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(trip, ActiveTripJsonContext.Default.ActiveTrip);
        return Enqueue(() =>
        {
            File.WriteAllBytes(_tempPath, json);
            File.Move(_tempPath, _path, overwrite: true);
        });
    }

    public Task DeleteAsync() => Enqueue(() =>
    {
        File.Delete(_path);
        File.Delete(_tempPath);
    });

    /// <summary>Completes once every save and delete so far has finished.</summary>
    public Task FlushAsync() => _writing;

    /// <returns>The trip, or null; and whether a file was there but couldn't be used.</returns>
    private (ActiveTrip? Trip, bool Damaged) Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return (null, false);
            }

            ActiveTrip? trip = JsonSerializer.Deserialize(File.ReadAllBytes(_path), ActiveTripJsonContext.Default.ActiveTrip);
            return trip?.IsValid() == true ? (trip, false) : (null, true);
        }
        catch (Exception ex)
        {
            // Whatever the file holds, a trip that can't be read is forgotten rather than failing every launch.
            System.Diagnostics.Debug.WriteLine($"Saved trip not read: {ex.GetType().Name}.");
            return (null, true);
        }
    }

    private Task Enqueue(Action write)
    {
        _writing = _writing.ContinueWith(_ =>
        {
            try
            {
                write();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"Saved trip not written: {ex.GetType().Name}.");
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return _writing;
    }
}
