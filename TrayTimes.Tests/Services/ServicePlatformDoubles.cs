using TrayTimes.Models;
using TrayTimes.Transit;

// Only platform boundaries are doubled: tests compile the production monitor, tracker, and tally.
namespace Microsoft.UI.Dispatching
{
    internal sealed class DispatcherQueue
    {
        public List<DispatcherQueueTimer> Timers { get; } = [];

        public DispatcherQueueTimer CreateTimer()
        {
            DispatcherQueueTimer timer = new();
            Timers.Add(timer);
            return timer;
        }

        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }

    internal sealed class DispatcherQueueTimer
    {
        public event EventHandler? Tick;

        public bool IsRepeating { get; set; }

        public TimeSpan Interval { get; set; }

        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Fire()
        {
            if (IsRunning)
            {
                if (!IsRepeating)
                {
                    Stop();
                }

                Tick?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

namespace TrayTimes.Services
{
    internal static class SettingsService
    {
        public static event EventHandler? StopsChanged;

        public static IReadOnlyList<SavedStop> Stops { get; set; } = [];

        public static ApiCallLog ApiCallLog { get; set; } = new();

        public static int HeadOutMinutes { get; set; } = 5;

        public static bool KeepAwakeWhileTracking => false;

        public static void RaiseStopsChanged() => StopsChanged?.Invoke(null, EventArgs.Empty);

        public static void Reset()
        {
            StopsChanged = null;
            Stops = [];
            HeadOutMinutes = 5;
        }
    }

    internal sealed class ScheduleService
    {
        public event EventHandler? Loaded;

        public IReadOnlyList<Arrival> AddScheduled(IReadOnlyList<Arrival> live, IReadOnlyCollection<string> stopIds) => live;

        public void RaiseLoaded() => Loaded?.Invoke(this, EventArgs.Empty);
    }

    internal static class ConnectivityService
    {
        public static bool IsInternetAvailable => true;
    }

    internal static class NotificationService
    {
        public static void Clear() { }

        public static void Show(string title, string text, bool canStopTracking) { }
    }
}

namespace Windows.Win32.System.Power
{
    internal enum EXECUTION_STATE : uint
    {
        ES_SYSTEM_REQUIRED = 1,
        ES_CONTINUOUS = 0x80000000,
    }
}

namespace Windows.Win32
{
    internal static class PInvoke
    {
        public static void SetThreadExecutionState(System.Power.EXECUTION_STATE state) { }
    }
}
