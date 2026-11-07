using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Controls;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Views;

namespace TrayTimes.ViewModels;

/// <summary>
/// The trip the flyout suggests, above the board: where the rider is likely headed and when to leave, or what's in the
/// way (setting up places, already there, no trip). Going to, beside it, picks another destination.
/// </summary>
internal sealed partial class SuggestionCardViewModel : ObservableObject
{
    private SuggestionCardItem _item = SuggestionCardItem.Empty;

    /// <summary>Raised when the rider selects the trip.</summary>
    public event EventHandler? TripInvoked;

    /// <summary>Raised when the rider picks where they're going: a favorite place's ID, or null to let the card guess.</summary>
    public event EventHandler<string?>? DestinationChosen;

    public event EventHandler? SearchRequested;

    public event EventHandler? SaveDestinationRequested;

    /// <summary>Raised when <see cref="Item"/> is a different trip or notice (not only new times), for the view to animate in.</summary>
    public event EventHandler? Entering;

    public SuggestionCardItem Item => _item;

    /// <summary>The place picked from Going to, checked in its menu; null when the card guesses.</summary>
    public string? DestinationId { get; set; }

    [ObservableProperty]
    public partial bool CanSaveDestination { get; set; }

    [ObservableProperty]
    public partial bool IsSaveDestinationEnabled { get; set; } = true;

    public string Title => _item.Title;

    public string Detail => _item.Detail;

    public string Note => _item.Note;

    public string Glyph => _item.Glyph;

    public string AutomationName => _item.AutomationName;

    public string TripToolTip => _item.TripToolTip;

    public string? ActionText => _item.ActionText;

    public IReadOnlyList<RouteChipItem> Routes => _item.Routes;

    public bool ShowsTrip => _item.ShowsTrip;

    public bool ShowsNotice => _item.ShowsNotice;

    public bool ShowsGlyph => _item.ShowsGlyph;

    public bool IsBusy => _item.IsBusy;

    public bool ShowsDetail => _item.ShowsDetail;

    public bool ShowsNote => _item.ShowsNote;

    public bool ShowsAction => _item.ShowsAction;

    public bool ShowsDestination => _item.ShowsDestination;

    public void ShowSaveDestination(bool canSave, bool isPlanning)
    {
        CanSaveDestination = canSave;
        IsSaveDestinationEnabled = !isPlanning;
    }

    /// <summary>Shows <paramref name="item"/>, announcing it as entering when it's a new trip or notice rather than new times.</summary>
    public void Show(SuggestionCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Signature == _item.Signature)
        {
            return;
        }

        bool entering = item.Key != _item.Key;
        _item = item;
        OnPropertyChanged(string.Empty); // Everything the item provides changed.
        if (entering)
        {
            Entering?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The Going to menu: the likely destination, each favorite place, then searching and editing places.</summary>
    public IReadOnlyList<DestinationOption> DestinationOptions() => new[] { new DestinationOption(AppStrings.Get("DestinationLikely"), null, null, DestinationId is null) }
        .Concat(SettingsService.Places.Select(p => new DestinationOption(p.Name, p.Id, MapHelpers.PlaceGlyph(p.Kind), p.Id == DestinationId)))
        .ToList();

    [RelayCommand]
    private void InvokeTrip() => TripInvoked?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RunAction() => _item.Action?.Invoke();

    [RelayCommand]
    private void SaveDestination() => SaveDestinationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ChooseDestination(string? placeId) => DestinationChosen?.Invoke(this, placeId);

    [RelayCommand]
    private void SearchDestination() => SearchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void EditPlaces() => App.Current.ShowSettings();
}

/// <summary>One choice in the Going to menu.</summary>
internal sealed record DestinationOption(string Text, string? PlaceId, string? Glyph, bool IsChecked);
