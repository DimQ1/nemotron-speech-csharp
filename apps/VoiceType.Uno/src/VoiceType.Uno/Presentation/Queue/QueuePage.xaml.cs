using System.ComponentModel;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using VoiceType.Uno.History;
using VoiceType.Uno.Services.Jobs;
using Windows.System;

namespace VoiceType.Uno.Presentation.Queue;

/// <summary>
/// Full-screen Queue page (design §4.5): <c>Frame.Navigate(typeof(QueuePage))</c>. Jobs keep
/// running after the page closes — they belong to the app-wide <see cref="JobRunner"/>.
/// </summary>
public sealed partial class QueuePage : Page
{
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public QueuePage()
    {
        ViewModel = new QueueViewModel(
            App.Services.GetRequiredService<JobQueue>(),
            App.Services.GetRequiredService<JobRunner>(),
            App.Services.GetRequiredService<RecordingStore>(),
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        InitializeComponent();
    }

    public QueueViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        UpdatePauseIcon();
        SubscribeSystemBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UnsubscribeSystemBack();
        ViewModel.Detach();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueueViewModel.IsQueuePaused))
            UpdatePauseIcon();
    }

    private void UpdatePauseIcon() => PauseAllIcon.Glyph = ViewModel.IsQueuePaused ? "" : "";

    /// <summary>⋯ of a card: Run now, Pause/Resume, Move up/down, Cancel / Cancel batch.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobCardViewModel card } anchor)
            return;

        var menu = new MenuFlyout();
        void Add(string text, bool enabled, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled, MinHeight = 44 };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (!card.IsRunning)
        {
            Add("Run now", card.CanRunNow, () => card.RunNowCommand.Execute(null));
            Add("Move up", card.CanMoveUp, card.MoveUp);
            Add("Move down", card.CanMoveDown, card.MoveDown);
        }
        if (card.CanResume)
            Add("Resume", true, () => card.ResumeCommand.Execute(null));
        if (card.IsRunning || card.CanPause)
            Add("Pause", true, () => card.PauseCommand.Execute(null));
        if (card.CanRetry)
            Add("Retry", true, () => card.RetryCommand.Execute(null));
        if (card.CanCancel)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(card.CancelLabel, true, () => card.CancelCommand.Execute(null));
        }
        if (menu.Items.Count == 0)
            Add("No actions", false, static () => { });

        menu.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    // ── Navigation (same pattern as ModelsPage) ─────────────────────────────

    private void GoBack()
    {
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.GoBack)
        {
            e.Handled = true;
            GoBack();
        }
    }

    private void SubscribeSystemBack()
    {
#if !VOICE_TYPE_WINDOWS
        try
        {
            _systemNavigation = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            _systemNavigation.BackRequested += OnSystemBackRequested;
        }
        catch (Exception ex)
        {
            _systemNavigation = null;
            Console.WriteLine($"[VoiceType.Uno] System back is not available: {ex.Message}");
        }
#endif
    }

    private void UnsubscribeSystemBack()
    {
#if !VOICE_TYPE_WINDOWS
        if (_systemNavigation is not null)
            _systemNavigation.BackRequested -= OnSystemBackRequested;
        _systemNavigation = null;
#endif
    }

#if !VOICE_TYPE_WINDOWS
    private void OnSystemBackRequested(object? sender, Windows.UI.Core.BackRequestedEventArgs e)
    {
        if (e.Handled || Frame?.CanGoBack != true)
            return;
        e.Handled = true;
        GoBack();
    }
#endif
}
