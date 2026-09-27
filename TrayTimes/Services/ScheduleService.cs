using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrayTimes.Models;
using TrayTimes.Transit;
using TrayTimes.Transit.Gtfs;
using Windows.Storage;

namespace TrayTimes.Services;

internal sealed class ScheduleFeedInfo
{
    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}

[JsonSerializable(typeof(ScheduleFeedInfo))]
internal sealed partial class ScheduleFeedJsonContext : JsonSerializerContext;

/// <summary>
/// The agency's published timetable, so stops show scheduled times where real-time predictions
/// don't reach. Keeps the GTFS zip in LocalState, checks for a new one weekly, and reads it into
/// memory on first use (about a second of work) until <see cref="Unload"/>.
/// </summary>
internal sealed class ScheduleService(Uri feedUri, StopIndexService stopIndex)
{
    private const string FeedFileName = "schedule.zip";
    private const string InfoFileName = "schedule.json";
    private const long MaxFeedBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>How far ahead scheduled times are listed.</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(3);

    private static readonly HttpClient Http = CreateHttpClient();

    private Task<GtfsTimetable?>? _loadTask;
    private DateTimeOffset _loadStartedAt;

    /// <summary>Raised (off the UI thread) when the timetable finishes loading and scheduled times become available.</summary>
    public event EventHandler? Loaded;

    /// <summary>
    /// <paramref name="live"/> plus scheduled arrivals at the stops beyond what's predicted (see
    /// <see cref="ArrivalMerge.WithSchedule"/>). Until the timetable has loaded this returns
    /// <paramref name="live"/> and starts loading it; <see cref="Loaded"/> says when to ask again.
    /// Call on the UI thread.
    /// </summary>
    public IReadOnlyList<Arrival> AddScheduled(IReadOnlyList<Arrival> live, IReadOnlyCollection<string> stopIds)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (_loadTask is { IsCompleted: true } done && (!done.IsCompletedSuccessfully || done.Result is null) && now - _loadStartedAt > RetryDelay)
        {
            _loadTask = null;
        }

        if (_loadTask is null)
        {
            _loadStartedAt = now;
            _loadTask = LoadAsync();
            _ = _loadTask.ContinueWith(
                t =>
                {
                    if (t.Result is not null)
                    {
                        Loaded?.Invoke(this, EventArgs.Empty);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }

        return _loadTask is { IsCompletedSuccessfully: true, Result: { } timetable } && stopIds.Count > 0
            ? ArrivalMerge.WithSchedule(live, timetable.GetArrivals(stopIds, now, now + Horizon))
            : live;
    }

    /// <summary>Drops the in-memory timetable (it reloads from disk on next use), unless it's loading.</summary>
    public void Unload()
    {
        if (_loadTask?.IsCompleted != false)
        {
            _loadTask = null;
        }
    }

    /// <summary>Null when the timetable can't be had right now (offline on first run, a bad download, no stop list).</summary>
    private async Task<GtfsTimetable?> LoadAsync()
    {
        try
        {
            // Started on the UI thread, which StopIndexService expects.
            IReadOnlyList<IndexedStop> directory = await stopIndex.GetAsync();
            return await Task.Run(async () =>
            {
                string path = await EnsureFeedAsync();
                await using FileStream zip = File.OpenRead(path);
                return GtfsTimetable.Load(zip, directory);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TransitApiException
            or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The feed's path on disk, downloading it if missing or due for a check.</summary>
    private async Task<string> EnsureFeedAsync()
    {
        string folder = ApplicationData.Current.LocalFolder.Path;
        string feedPath = Path.Combine(folder, FeedFileName);
        string infoPath = Path.Combine(folder, InfoFileName);
        bool haveFeed = File.Exists(feedPath);
        ScheduleFeedInfo? info = haveFeed ? await TryReadInfoAsync(infoPath) : null;
        if (info is not null && DateTimeOffset.Now - info.CheckedAt < CheckInterval)
        {
            return feedPath;
        }

        try
        {
            await DownloadAsync(feedPath, infoPath, info);
        }
        catch (Exception ex) when (haveFeed && ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            // Keep using the copy we have; the next load checks again.
        }

        return feedPath;
    }

    /// <summary>Fetches the feed unless the server says our copy (<paramref name="current"/>) is still current.</summary>
    private async Task DownloadAsync(string feedPath, string infoPath, ScheduleFeedInfo? current)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, feedUri);
        if (current?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        request.Headers.IfModifiedSince = current?.LastModified;

        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotModified && current is not null)
        {
            current.CheckedAt = DateTimeOffset.Now;
            await WriteInfoAsync(infoPath, current);
            return;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxFeedBytes)
        {
            throw new InvalidDataException("The schedule download is unexpectedly large.");
        }

        string tempPath = feedPath + ".download";
        await using (FileStream file = File.Create(tempPath))
        {
            await response.Content.CopyToAsync(file);
        }

        try
        {
            // Make sure it's a readable zip before replacing a good copy with it.
            using (ZipArchive check = ZipFile.OpenRead(tempPath))
            {
                _ = check.Entries.Count;
            }

            File.Move(tempPath, feedPath, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }

        await WriteInfoAsync(infoPath, new ScheduleFeedInfo
        {
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified,
            CheckedAt = DateTimeOffset.Now,
        });
    }

    private static async Task<ScheduleFeedInfo?> TryReadInfoAsync(string path)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, ScheduleFeedJsonContext.Default.ScheduleFeedInfo);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteInfoAsync(string path, ScheduleFeedInfo info)
    {
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, info, ScheduleFeedJsonContext.Default.ScheduleFeedInfo);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TrayTimes/1.0");
        return client;
    }
}
