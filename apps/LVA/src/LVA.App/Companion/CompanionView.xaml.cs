using System.Timers;

namespace LVA.App.Companion;

/// <summary>
/// Companion widget: renders the pet, runs the animation clock (~30 FPS),
/// tracks the pointer for the gaze, and maps pointer gestures to interactive
/// states (petting / poked / carried) per TZ §14.4.
/// </summary>
public partial class CompanionView : ContentView
{
    public static readonly BindableProperty StateMachineProperty =
        BindableProperty.Create(nameof(StateMachine), typeof(CompanionStateMachine), typeof(CompanionView),
            propertyChanged: OnStateMachineChanged);

    private readonly System.Timers.Timer _animationTimer;
    private readonly IDispatcherTimer _pettingTimer;
    private CompanionDrawable? _drawable;
    private DateTime _startedAt = DateTime.UtcNow;
    private bool _pointerInside;
    private DateTime _pointerDownAt;

    public CompanionView()
    {
        InitializeComponent();

        _animationTimer = new System.Timers.Timer(33); // ~30 FPS
        _animationTimer.Elapsed += OnAnimationTick;

        _pettingTimer = Dispatcher.CreateTimer();
        _pettingTimer.Interval = TimeSpan.FromMilliseconds(600);
        _pettingTimer.Tick += OnPettingTimeout;
    }

    public CompanionStateMachine? StateMachine
    {
        get => (CompanionStateMachine?)GetValue(StateMachineProperty);
        set => SetValue(StateMachineProperty, value);
    }

    private static void OnStateMachineChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (CompanionView)bindable;
        if (newValue is CompanionStateMachine sm)
        {
            view._drawable = new CompanionDrawable(sm);
            view.Graphics.Drawable = view._drawable;
            sm.StateChanged += _ => view.Graphics.Invalidate();
            view._animationTimer.Start();
        }
    }

    private void OnAnimationTick(object? sender, ElapsedEventArgs e)
    {
        if (_drawable is null) return;
        _drawable.Time = (DateTime.UtcNow - _startedAt).TotalSeconds;

        // Pointer tracking → Curious when close (TZ §14.4.1).
        Dispatcher.Dispatch(() =>
        {
            if (_pointerInside && StateMachine?.Current is CompanionState.Idle or CompanionState.Curious)
                StateMachine.SetBase(CompanionState.Curious);
            Graphics.Invalidate();
        });
    }

    /// <summary>Updates the pointer offset (called by the host page on pointer move).</summary>
    public void UpdatePointer(Point position)
    {
        if (_drawable is null) return;
        var cx = Graphics.Width / 2;
        var cy = Graphics.Height / 2;
        _drawable.PointerOffset = new PointF(
            (float)Math.Clamp((position.X - cx) / 140.0, -1, 1),
            (float)Math.Clamp((position.Y - cy) / 140.0, -1, 1));
        _pointerInside = position.X >= 0 && position.X <= Graphics.Width &&
                         position.Y >= 0 && position.Y <= Graphics.Height;

        if (_pointerInside && !_pettingTimer.IsRunning)
            _pettingTimer.Start();
        else if (!_pointerInside && _pettingTimer.IsRunning)
            _pettingTimer.Stop();
    }

    /// <summary>Pointer pressed — start potential carry; click resolves to Poked.</summary>
    public void OnPointerPressed()
    {
        _pointerDownAt = DateTime.UtcNow;
        StateMachine?.SetBase(CompanionState.Carried);
    }

    /// <summary>Pointer released — if quick, it's a poke; otherwise a landing.</summary>
    public void OnPointerReleased()
    {
        if (StateMachine is null) return;
        if ((DateTime.UtcNow - _pointerDownAt).TotalMilliseconds < 250)
            _ = StateMachine.OverrideForAsync(CompanionState.Poked, TimeSpan.FromMilliseconds(500));
        else
            _ = StateMachine.OverrideForAsync(CompanionState.Greeting, TimeSpan.FromMilliseconds(900));
    }

    private void OnPettingTimeout(object? sender, EventArgs e)
    {
        _pettingTimer.Stop();
        if (_pointerInside && StateMachine?.Current is CompanionState.Idle or CompanionState.Curious)
            StateMachine.SetBase(CompanionState.Petting);
    }

    /// <summary>Sets the audio level for the listening pulse.</summary>
    public void SetAudioLevel(float level)
    {
        if (_drawable is not null)
            _drawable.AudioLevel = level;
    }
}
