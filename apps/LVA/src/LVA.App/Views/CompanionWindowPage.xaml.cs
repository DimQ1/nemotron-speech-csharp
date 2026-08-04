using LVA.App.Companion;

namespace LVA.App.Views;

/// <summary>
/// Standalone companion window content: frameless, always-on-top, draggable
/// across the desktop (TZ §14.2 CompanionStandalone). Drag uses pointer
/// capture and asks the window manager to move the native window.
/// </summary>
public partial class CompanionWindowPage : ContentPage
{
    private readonly CompanionStateMachine _stateMachine;
    private bool _dragging;
    private Point _dragStart;
    private DateTime _pressAt;

    /// <summary>Raised on pointer press to start the native window move loop.</summary>
    public event Action? NativeDragRequested;

    public CompanionWindowPage(CompanionStateMachine stateMachine)
    {
        InitializeComponent();
        _stateMachine = stateMachine;

        Loaded += (_, _) => Companion.StateMachine = stateMachine;

        var pointerGesture = new PointerGestureRecognizer();
        pointerGesture.PointerMoved += (_, e) =>
        {
            var pos = e.GetPosition(Companion);
            if (pos is not null)
                Companion.UpdatePointer(pos.Value);
        };
        Companion.GestureRecognizers.Add(pointerGesture);
    }

    private void OnPointerPressed(object? sender, PointerEventArgs e)
    {
        _dragging = true;
        _pressAt = DateTime.UtcNow;
        _dragStart = e.GetPosition(this) ?? new Point(0, 0);
        Companion.OnPointerPressed(); // Carried state
        NativeDragRequested?.Invoke();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Native WM_NCHITTEST handles dragging; keep gaze tracking only.
    }

    private void OnPointerReleased(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        Companion.OnPointerReleased();
    }

    public void SetBubble(string text) => BubbleLabel.Text = text;
    public CompanionView Pet => Companion;
}
