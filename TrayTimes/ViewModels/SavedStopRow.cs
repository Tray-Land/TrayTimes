using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayTimes.Models;

namespace TrayTimes.ViewModels;

/// <summary>
/// One saved stop in the Settings list. Its nickname changes in place, so naming a stop doesn't rebuild the
/// rows the user is working in (the text box being left, or a button being pressed).
/// </summary>
internal sealed partial class SavedStopRow(SavedStop stop, Action<SavedStopRow> remove, Action<SavedStopRow, string?> setNickname) : ObservableObject
{
    public SavedStop Stop { get; private set; } = stop;

    public string StopName => Stop.StopName;

    public string Description => Stop.Description;

    public string Nickname => Stop.Nickname ?? string.Empty;

    /// <summary>Takes on <paramref name="nickname"/> (null for none), as the box for it and its sibling rows show.</summary>
    public void Rename(string? nickname)
    {
        if (Stop.Nickname == nickname)
        {
            return;
        }

        Stop = Stop with { Nickname = nickname };
        OnPropertyChanged(nameof(Nickname));
    }

    /// <summary>Saves the typed nickname (blank for none) for this stop, if it changed.</summary>
    [RelayCommand]
    private void SaveNickname(string? text)
    {
        string? nickname = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (nickname != Stop.Nickname)
        {
            setNickname(this, nickname);
        }
    }

    [RelayCommand]
    private void Remove() => remove(this);
}
