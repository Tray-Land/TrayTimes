using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Models;
using TrayTimes.Services;
using TrayTimes.Views;

namespace TrayTimes.ViewModels;

/// <summary>
/// A custom favorite place in Settings: a name and an address. The place is saved once it has an address,
/// and kept up to date as its name or address changes.
/// </summary>
internal sealed partial class CustomPlaceViewModel : ObservableObject
{
    private readonly FavoritePlace? _existing;
    private readonly Action<CustomPlaceViewModel> _removed;
    private readonly bool _initialized;

    public CustomPlaceViewModel(FavoritePlace? existing, Action<CustomPlaceViewModel> removed)
    {
        _existing = existing;
        _removed = removed;
        Id = existing?.Id ?? Guid.NewGuid().ToString("N");
        Name = existing?.Name ?? string.Empty;
        Place = existing?.Place;
        _initialized = true;
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Place? Place { get; set; }

    public string NameAutomationId => $"PlaceName_{Id}";

    public string RemoveAutomationId => $"RemovePlace_{Id}";

    public string RemoveLabel => AppStrings.Format("SettingsRemovePlaceFormat", DisplayName);

    public string AddressLabel => AppStrings.Format("SettingsPlaceAddressFormat", DisplayName);

    private string DisplayName => string.IsNullOrWhiteSpace(Name) ? AppStrings.Get("SettingsCustomPlaceDefaultName") : Name.Trim();

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(RemoveLabel));
        OnPropertyChanged(nameof(AddressLabel));
        if (_initialized && Place is not null)
        {
            Save();
        }
    }

    partial void OnPlaceChanged(Place? value)
    {
        if (_initialized)
        {
            Save();
        }
    }

    private void Save()
    {
        if (Place is { } place)
        {
            SettingsService.SavePlace(new FavoritePlace(Id, PlaceKind.Custom, DisplayName, place,
                StopId: place == _existing?.Place ? _existing?.StopId : null));
        }
        else
        {
            SettingsService.RemovePlace(Id);
        }
    }

    [RelayCommand]
    private void Remove()
    {
        SettingsService.RemovePlace(Id);
        _removed(this);
    }
}
