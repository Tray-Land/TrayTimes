using CommunityToolkit.Mvvm.ComponentModel;
using TrayTimes.Services;
using TrayTimes.Transit;

namespace TrayTimes.ViewModels;

/// <summary>
/// A route's service alerts as a warning sign, red when one suspends service, that opens their details. The sign is
/// hidden while the route has none.
/// </summary>
internal sealed partial class AlertsButtonViewModel : ObservableObject
{
    private IReadOnlyList<ServiceAlert> _alerts = [];
    private string _route = string.Empty;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCaution))]
    public partial bool IsSevere { get; set; }

    public bool ShowsCaution => !IsSevere;

    [ObservableProperty]
    public partial string SignName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Heading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<AlertEntry> Entries { get; set; } = [];

    /// <summary>Shows <paramref name="alerts"/> (most severe first; none hides the sign) for the route displayed as <paramref name="route"/>.</summary>
    public void Update(IReadOnlyList<ServiceAlert>? alerts, string route)
    {
        _alerts = alerts ?? [];
        _route = route;
        IsVisible = _alerts.Count > 0;
        if (!IsVisible)
        {
            return;
        }

        IsSevere = _alerts.Any(a => a.Severity == AlertSeverity.Severe);
        SignName = AlertMessages.SignName(route, _alerts);
    }

    /// <summary>Fills in the details as they open, so their "until" and "starts" times read from now.</summary>
    public void RefreshEntries(DateTimeOffset now)
    {
        Heading = AlertMessages.Heading(_route);
        Entries = _alerts.Select(a => new AlertEntry(a, now)).ToList();
    }
}

/// <summary>One alert in the details: its headline, then what it does, why, and when, then its text.</summary>
public sealed class AlertEntry(ServiceAlert alert, DateTimeOffset now)
{
    public string Title { get; } = AlertMessages.Title(alert);

    public string Summary { get; } = AlertMessages.Summary(alert, now);

    public string Body { get; } = AlertMessages.Body(alert);

    public bool ShowsSummary => Summary.Length > 0;

    public bool ShowsBody => Body.Length > 0;
}
