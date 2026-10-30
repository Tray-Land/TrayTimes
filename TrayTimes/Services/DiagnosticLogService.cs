using System.Globalization;
using TrayTimes.Diagnostics;
using TrayTimes.Transit;
using Windows.Storage;

namespace TrayTimes.Services;

/// <summary>
/// The app's small on-disk log, <c>LocalState\logs\tray-times.log</c> (plus one <c>.old</c> archive), for
/// working out why data is missing. It holds finished transit calls and what the vehicles feed returned;
/// never the key, URLs, stops, or locations. Calls are limited by the call budget, so it stays short.
/// </summary>
internal static class DiagnosticLogService
{
    private static readonly Lazy<RollingFileLog> Log = new(() =>
        new RollingFileLog(Path.Combine(ApplicationData.Current.LocalFolder.Path, "logs", "tray-times.log")));

    public static string FilePath => Log.Value.FilePath;

    public static void Write(string message) => Log.Value.Write(message);

    /// <summary>Logs a finished call. Starts and cache or task reuse are left out: they would only repeat it.</summary>
    public static void Observe(TransitRequestObservation observation)
    {
        System.Diagnostics.Debug.WriteLine(observation);
        if (observation.Phase != TransitRequestPhase.Completed)
        {
            return;
        }

        Write(string.Create(CultureInfo.InvariantCulture,
            $"request endpoint={observation.Endpoint} consumer={observation.Consumer} trigger={observation.Trigger} outcome={observation.Outcome} http={observation.HttpStatus} ms={observation.Elapsed.TotalMilliseconds:F0} batch={observation.BatchSize}"));
    }
}
