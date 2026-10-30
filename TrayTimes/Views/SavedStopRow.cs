using System.ComponentModel;
using TrayTimes.Models;

namespace TrayTimes.Views;

/// <summary>
/// One saved stop in the Settings list. Its nickname changes in place, so naming a stop doesn't rebuild the
/// rows the user is working in (the text box being left, or a button being pressed).
/// </summary>
internal sealed class SavedStopRow(SavedStop stop) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Nickname)));
    }
}
