using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrayTimes.Commute;
using TrayTimes.Transit;
using TrayTimes.ViewModels;
using Windows.System;

namespace TrayTimes.Controls;

/// <summary>Searches for a trip's destination. The search is in <see cref="DestinationSearchViewModel"/>; this view focuses the box and passes keys and clicks on.</summary>
public sealed partial class DestinationSearchView : UserControl
{
    public DestinationSearchView()
    {
        ViewModel = new DestinationSearchViewModel();
        ViewModel.BackRequested += (_, e) => BackRequested?.Invoke(this, e);
        ViewModel.DestinationChosen += (_, result) => DestinationChosen?.Invoke(this, result);
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(Search_KeyDown), handledEventsToo: true);
    }

    public event EventHandler? BackRequested;

    public event EventHandler<DestinationResult>? DestinationChosen;

    internal DestinationSearchViewModel ViewModel { get; }

    public void Open(ITransitService service, GeoPoint? near)
    {
        ViewModel.Open(service, near);
        FocusQuery();
    }

    public void Cancel() => ViewModel.Cancel();

    /// <summary>
    /// Puts the cursor in the search box. The view has usually only just become visible (or been created), so
    /// focus is retried once the layout pass has run.
    /// </summary>
    private void FocusQuery()
    {
        UpdateLayout();
        if (!QueryBox.Focus(FocusState.Programmatic))
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => QueryBox.Focus(FocusState.Programmatic));
        }
    }

    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.BackCommand.Execute(null);
        }
    }

    private void QueryBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        ViewModel.SubmitCommand.Execute(args.QueryText);

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.ChooseCommand.Execute(e.ClickedItem as DestinationSearchItem);
}
