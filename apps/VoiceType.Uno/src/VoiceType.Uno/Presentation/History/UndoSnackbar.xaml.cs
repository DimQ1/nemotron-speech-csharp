using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

/// <summary>Shows the newest pending delete of <see cref="HistoryServices.Deletes"/> with Undo.</summary>
public sealed partial class UndoSnackbar : UserControl
{
    private PendingDelete? _shown;

    public UndoSnackbar() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HistoryServices.Deletes.Changed += Refresh;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => HistoryServices.Deletes.Changed -= Refresh;

    private void Refresh()
    {
        _shown = HistoryServices.Deletes.Pending.Latest;
        Visibility = _shown is null ? Visibility.Collapsed : Visibility.Visible;
        if (_shown is not null)
            Label.Text = _shown.Label;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is { } shown)
            HistoryServices.Deletes.Undo(shown.Token);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is { } shown)
            HistoryServices.Deletes.CommitNow(shown.Token);
    }
}
