using TrayTimes.Transit;

namespace TrayTimes.Services;

/// <summary>Keeps a saved tally of transit API calls so the Settings page can show how often they're hit.</summary>
internal static class ApiUsageService
{
    private static readonly Lock Gate = new();
    private static ApiCallLog _log = SettingsService.ApiCallLog;

    /// <summary>Raised after a call is recorded or the tally is cleared. May fire on any thread.</summary>
    public static event EventHandler? Changed;

    public static void Observe(TransitRequestObservation observation) =>
        System.Diagnostics.Debug.WriteLine(observation);

    public static void Record(string endpoint)
    {
        lock (Gate)
        {
            _log.Record(endpoint, Today());
            SettingsService.ApiCallLog = _log;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static ApiCallSummary GetSummary()
    {
        lock (Gate)
        {
            return _log.Summarize(Today());
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            _log = new ApiCallLog();
            SettingsService.ApiCallLog = _log;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);
}
