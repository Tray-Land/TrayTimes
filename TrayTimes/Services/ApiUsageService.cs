using TrayTimes.Transit;

namespace TrayTimes.Services;

/// <summary>Keeps a saved tally of transit API calls so the Settings page can show them and the call budget can pace itself.</summary>
internal static class ApiUsageService
{
    private static readonly Lock Gate = new();
    private static ApiCallLog _log = SettingsService.ApiCallLog;

    // The tally is never reset by the rider: the call budget paces itself from it, and the
    // provider's monthly quota doesn't reset with it.

    /// <summary>Raised after a call is recorded or the tally is cleared. May fire on any thread.</summary>
    public static event EventHandler? Changed;

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

    /// <summary>Calls today and this month, for the call budget.</summary>
    public static TransitUsage GetUsage()
    {
        lock (Gate)
        {
            return _log.Usage(Today());
        }
    }

    /// <summary>Starts a new tally: the old one counted calls to a different provider.</summary>
    public static void StartOver()
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
