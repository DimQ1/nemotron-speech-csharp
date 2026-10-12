using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VoiceType.Uno.Services.Playback;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Player for a recording (design §4.3 "Плеер", §6.3): seek bar with segment ticks,
/// elapsed / total, −10 s, Play/Pause (56 px, centred), +10 s, speed (0.75–2×, pitch kept).
/// Owns one <see cref="IAudioPlayer"/> from <see cref="IAudioPlayerFactory"/>; all events are
/// raised on the UI thread. Playback stops when the control is unloaded.
/// </summary>
public sealed partial class PlayerBar : UserControl
{
    /// <summary>How long a seek-bar drag waits before seeking (each seek restarts the device).</summary>
    private static readonly TimeSpan SeekDebounce = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _seekTimer;
    private IAudioPlayer? _player;
    private IReadOnlyList<double> _ticks = [];
    private TimeSpan _duration;
    private TimeSpan _position;
    private bool _updatingSlider;
    private DateTime _ignorePlayerPositionUntil;
    private int _loadVersion;
    private Func<double, int, double?>? _turnNavigator;

    public PlayerBar()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _seekTimer = _dispatcher.CreateTimer();
        _seekTimer.Interval = SeekDebounce;
        _seekTimer.IsRepeating = false;
        _seekTimer.Tick += (_, _) => Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        UpdateSpeedText(1.0);
    }

    /// <summary>Playback position changed (≈20 Hz while playing, and on every seek). UI thread.</summary>
    public event Action<TimeSpan>? PositionChanged;

    /// <summary>Play/pause state changed. UI thread.</summary>
    public event Action<bool>? PlayingChanged;

    /// <summary>Playback reached the end. UI thread.</summary>
    public event Action? Ended;

    public TimeSpan Position => _position;

    public TimeSpan Duration => _duration;

    public bool IsPlaying => _player?.IsPlaying ?? false;

    /// <summary>Opens <paramref name="audioPath"/> (Ogg Opus or WAV), paused at 0. Shows an inline error when it cannot be played.</summary>
    public async Task LoadAsync(string audioPath)
    {
        Unload();
        int version = ++_loadVersion;
        var player = CreatePlayer();
        try
        {
            await player.LoadAsync(audioPath);
        }
        catch (Exception ex)
        {
            await player.DisposeAsync();
            if (version == _loadVersion) ShowError($"Can't open the audio: {ex.Message}");
            return;
        }
        if (version != _loadVersion) // a newer load or Unload won
        {
            await player.DisposeAsync();
            return;
        }

        _player = player;
        player.PositionChanged += OnPlayerPositionChanged;
        player.PlayingChanged += OnPlayerPlayingChanged;
        player.Ended += OnPlayerEnded;

        _duration = player.Duration;
        _position = TimeSpan.Zero;
        ErrorText.Visibility = Visibility.Collapsed;
        SetSliderValue(0, Math.Max(_duration.TotalSeconds, 0.001));
        SetControlsEnabled(_duration > TimeSpan.Zero);
        UpdateTimeText();
        UpdatePlayIcon(false);
        UpdateSpeedText(player.Speed);
        DrawTicks();
    }

    /// <summary>Moves the playhead (e.g. a tap on a transcript segment); keeps playing if it was.</summary>
    public void Seek(TimeSpan position)
    {
        _seekTimer.Stop();
        if (_player is null) return;
        position = PlaybackCursor.Clamp(position, _duration);
        _ignorePlayerPositionUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(150);
        _player.Seek(position);
        ApplyPosition(position);
    }

    public void Play()
    {
        try
        {
            _player?.Play();
        }
        catch (Exception ex)
        {
            ShowError($"Can't play: {ex.Message}");
        }
    }

    public void Pause() => _player?.Pause();

    /// <summary>Stops playback and releases the audio device and file.</summary>
    public void Unload()
    {
        _loadVersion++;
        _seekTimer.Stop();
        var player = _player;
        _player = null;
        if (player is not null)
        {
            player.PositionChanged -= OnPlayerPositionChanged;
            player.PlayingChanged -= OnPlayerPlayingChanged;
            player.Ended -= OnPlayerEnded;
            _ = player.DisposeAsync().AsTask();
        }
        _duration = _position = TimeSpan.Zero;
        SetSliderValue(0, 1);
        SetControlsEnabled(false);
        UpdatePlayIcon(false);
        UpdateTimeText();
        TickCanvas.Children.Clear();
    }

    /// <summary>Segment starts as fractions of the duration (<see cref="PlaybackCursor.SegmentTicks"/>); drawn as ticks on the seek bar.</summary>
    public void SetTicks(IReadOnlyList<double> fractions)
    {
        _ticks = fractions ?? [];
        DrawTicks();
    }

    private static IAudioPlayer CreatePlayer()
    {
        IAudioPlayerFactory? factory = null;
        try
        {
            factory = App.Services.GetService<IAudioPlayerFactory>();
        }
        catch (InvalidOperationException)
        {
            // Host not built (designer / tests): fall back to the platform factory.
        }
        return (factory ?? new AudioPlayerFactory()).Create();
    }

    private void OnPlayerPositionChanged(TimeSpan position) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_player is null || DateTime.UtcNow < _ignorePlayerPositionUntil || _seekTimer.IsRunning) return;
            ApplyPosition(position);
        });

    private void OnPlayerPlayingChanged(bool playing) =>
        _dispatcher.TryEnqueue(() =>
        {
            UpdatePlayIcon(playing);
            PlayingChanged?.Invoke(playing);
        });

    private void OnPlayerEnded() =>
        _dispatcher.TryEnqueue(() =>
        {
            ApplyPosition(_duration);
            Ended?.Invoke();
        });

    private void ApplyPosition(TimeSpan position)
    {
        _position = position;
        SetSliderValue(position.TotalSeconds, SeekSlider.Maximum);
        UpdateTimeText();
        PositionChanged?.Invoke(position);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        if (_player.IsPlaying) Pause();
        else Play();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_turnNavigator is not null)
        {
            SeekToTurn(-1);
            return;
        }
        Seek(PlaybackCursor.Skip(_position, -PlaybackCursor.SkipInterval, _duration));
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_turnNavigator is not null)
        {
            SeekToTurn(1);
            return;
        }
        Seek(PlaybackCursor.Skip(_position, PlaybackCursor.SkipInterval, _duration));
    }

    private void SeekToTurn(int direction)
    {
        if (_turnNavigator?.Invoke(_position.TotalSeconds, direction) is { } seconds)
            Seek(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Skip-by-turn mode (speaker filter): the −10 / +10 s buttons become "Previous / Next turn by <paramref name="speakerName"/>".
    /// <paramref name="navigator"/> gets the position in seconds and −1 / +1 and returns the turn start to jump to, or null
    /// when there is none. Pass null to return to ±10 s. Everything else about the control is unchanged.
    /// </summary>
    public void SetTurnSkip(Func<double, int, double?>? navigator, string? speakerName)
    {
        _turnNavigator = navigator;
        var turn = navigator is not null;
        BackIcon.Symbol = turn ? Symbol.Previous : Symbol.Undo;
        ForwardIcon.Symbol = turn ? Symbol.Next : Symbol.Redo;
        BackSeconds.Visibility = ForwardSeconds.Visibility = turn ? Visibility.Collapsed : Visibility.Visible;
        var back = turn ? $"Previous turn by {speakerName}" : "Back 10 seconds";
        var forward = turn ? $"Next turn by {speakerName}" : "Forward 10 seconds";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BackButton, back);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ForwardButton, forward);
        ToolTipService.SetToolTip(BackButton, back);
        ToolTipService.SetToolTip(ForwardButton, forward);
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        _player.Speed = PlaybackCursor.NextSpeed(_player.Speed);
        UpdateSpeedText(_player.Speed);
    }

    private void OnSeekSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _player is null) return;
        // User drag / keyboard: show the target at once, seek when the thumb rests.
        _position = TimeSpan.FromSeconds(e.NewValue);
        UpdateTimeText();
        _seekTimer.Stop();
        _seekTimer.Start();
    }

    private void SetSliderValue(double value, double maximum)
    {
        _updatingSlider = true;
        try
        {
            SeekSlider.Maximum = maximum;
            SeekSlider.Value = Math.Clamp(value, 0, maximum);
        }
        finally
        {
            _updatingSlider = false;
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        SeekSlider.IsEnabled = enabled;
        BackButton.IsEnabled = enabled;
        PlayButton.IsEnabled = enabled;
        ForwardButton.IsEnabled = enabled;
        SpeedButton.IsEnabled = enabled;
    }

    private void UpdateTimeText() =>
        TimeText.Text = $"{PlaybackCursor.FormatTime(_position, _duration)} / {PlaybackCursor.FormatTime(_duration, _duration)}";

    private void UpdatePlayIcon(bool playing)
    {
        PlayIcon.Symbol = playing ? Symbol.Pause : Symbol.Play;
        var label = playing ? "Pause" : "Play";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, label);
        ToolTipService.SetToolTip(PlayButton, label);
    }

    private void UpdateSpeedText(double speed) => SpeedText.Text = PlaybackCursor.FormatSpeed(speed);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnTickCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawTicks();

    private void DrawTicks()
    {
        TickCanvas.Children.Clear();
        double width = TickCanvas.ActualWidth;
        if (width <= 0 || _duration <= TimeSpan.Zero) return;
        var brush = Application.Current.Resources.TryGetValue("TextFillColorTertiaryBrush", out var value) && value is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
        foreach (var f in _ticks)
        {
            var tick = new Rectangle { Width = 1, Height = 10, Fill = brush };
            Canvas.SetLeft(tick, Math.Round(f * width));
            TickCanvas.Children.Add(tick);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unload();
}
