using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using VoiceType.Uno.Services;
using Windows.System;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>
/// Keyboard shortcuts (desktop): three key-chip fields that record a combination. A shortcut another app already owns is
/// shown in its row ("Not registered") and in a banner at the top.
/// </summary>
internal sealed class HotkeysSection : SettingsSectionView
{
    private readonly Dictionary<HotkeyAction, HotkeyField> _fields = [];
    private StackPanel? _bannerHost;

    public HotkeysSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var stack = new StackPanel();
        _bannerHost = new StackPanel();
        stack.Children.Add(_bannerHost);

        stack.Children.Add(SettingsRows.SectionHeader("Work in any app"));
        var rows = new List<UIElement>();
        foreach (var (action, icon) in new[]
                 {
                     (HotkeyAction.ToggleRecording, Symbol.Microphone),
                     (HotkeyAction.ToggleMute, Symbol.Mute),
                     (HotkeyAction.InjectText, Symbol.Keyboard),
                 })
        {
            var field = new HotkeyField(this, Ctx, action, icon);
            _fields[action] = field;
            rows.Add(field.Row);
        }
        stack.Children.Add(SettingsRows.Card([.. rows]));
        stack.Children.Add(SettingsRows.Note(HotkeyText.Help));
        RefreshRegistration();
        return stack;
    }

    public override void OnShown() => Ctx.Main.HotkeyRegistrationChanged += OnRegistrationChanged;

    public override void OnHidden() => Ctx.Main.HotkeyRegistrationChanged -= OnRegistrationChanged;

    private void OnRegistrationChanged() => DispatcherQueue?.TryEnqueue(RefreshRegistration);

    /// <summary>Marks the rows whose shortcut is not registered and shows the banner.</summary>
    private void RefreshRegistration()
    {
        var failed = Ctx.Main.FailedHotkeys;
        foreach (var (action, field) in _fields)
            field.SetNotRegistered(failed.Any(f => f.Action == action));

        if (_bannerHost is null)
            return;
        _bannerHost.Children.Clear();
        var text = HotkeyText.ConflictBanner(failed);
        if (text.Length > 0)
            _bannerHost.Children.Add(SettingsRows.Callout(this, text, warn: true));
    }
}

/// <summary>One shortcut row: title, state line and the chip button that records a new combination.</summary>
internal sealed class HotkeyField
{
    private readonly SettingsContext _ctx;
    private readonly HotkeyAction _action;
    private readonly FrameworkElement _scope;
    private readonly Button _button;
    private readonly Border _frame;
    private readonly TextBlock _detail;
    private readonly string _baseDetail;
    private readonly HashSet<VirtualKey> _modifiers = [];
    private bool _recording;
    private bool _notRegistered;
    private long _lastCommitTick = long.MinValue / 2;

    public HotkeyField(FrameworkElement scope, SettingsContext ctx, HotkeyAction action, Symbol icon)
    {
        _scope = scope;
        _ctx = ctx;
        _action = action;
        _baseDetail = HotkeyText.Detail(action);

        _button = new Button
        {
            MinHeight = 44,
            MinWidth = 44,
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(_button, HotkeyText.Title(action) + " shortcut");
        _button.Click += (_, _) =>
        {
            // The key that ended a recording (Space, Enter) would click the button again on key-up.
            if (Environment.TickCount64 - _lastCommitTick > 400)
                StartRecording();
        };
        _button.LostFocus += (_, _) => StopRecording(cancelled: true);
        // Space and Enter are keys a shortcut may contain: take them before the button turns them into a click.
        _button.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        _button.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnKeyUp), handledEventsToo: true);

        _frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = SettingsRows.SystemBrush("ControlStrokeColorDefaultBrush"),
            Background = SettingsRows.SystemBrush("ControlFillColorSecondaryBrush"),
            Child = _button,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Row = SettingsRows.Row(SettingsRows.Icon(icon), HotkeyText.Title(action), _baseDetail, _frame, out _detail);
        RenderChips();
    }

    public Grid Row { get; }

    public void SetNotRegistered(bool value)
    {
        _notRegistered = value;
        RenderChips();
        SetDetail(value ? HotkeyText.NotRegistered : _baseDetail, warn: value);
    }

    private string Chord => HotkeyText.Chord(_ctx.Live, _action);

    private void StartRecording()
    {
        _recording = true;
        _modifiers.Clear();
        SetDetail(_baseDetail, warn: false);
        RenderChips();
        // A combination that is registered right now would run its action instead of reaching this field.
        _ = _ctx.Main.SetHotkeysSuspendedAsync(true);
    }

    /// <summary>Leaves recording. A cancelled recording (<paramref name="cancelled"/>) registers the shortcuts again at once; a finished one does so after its change is applied.</summary>
    private void StopRecording(bool cancelled)
    {
        if (!_recording)
            return;
        _recording = false;
        _modifiers.Clear();
        if (cancelled)
        {
            RenderChips();
            _ = _ctx.Main.SetHotkeysSuspendedAsync(false);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
            return;
        e.Handled = true;

        if (NormalizeModifier(e.Key) is { } modifier)
        {
            _modifiers.Add(modifier);
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            StopRecording(cancelled: false);
            Commit("");
            return;
        }

        // Keys without a name (punctuation, IME packets) cannot be registered as a shortcut: ignore them.
        if (KeyToString(e.Key) is not { } keyName)
            return;
        var chord = HotkeyText.Compose(
            _modifiers.Contains(VirtualKey.Control), _modifiers.Contains(VirtualKey.Shift),
            _modifiers.Contains(VirtualKey.Menu), _modifiers.Contains(VirtualKey.LeftWindows), keyName);

        if (HotkeyText.Validate(chord) is { } error)
        {
            SetDetail(error, warn: true);
            return;
        }
        if (HotkeyText.DuplicateOf(_ctx.Live, _action, chord) is { } other)
        {
            SetDetail(HotkeyText.DuplicateMessage(chord, other), warn: true);
            return;
        }

        StopRecording(cancelled: false);
        Commit(chord);
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_recording && NormalizeModifier(e.Key) is { } modifier)
            _modifiers.Remove(modifier);
    }

    private void Commit(string chord)
    {
        _lastCommitTick = Environment.TickCount64;
        SetDetail(_baseDetail, warn: false);
        _ = CommitAsync(chord);
    }

    private async Task CommitAsync(string chord)
    {
        await _ctx.ChangeAsync(s => HotkeyText.SetChord(s, _action, chord));
        // Registers the shortcuts again (also when the chord did not change, so nothing stays released).
        await _ctx.Main.SetHotkeysSuspendedAsync(false);
        _notRegistered = false;
        RenderChips();
    }

    /// <summary>The chips of the saved chord, the recording prompt, or "No shortcut".</summary>
    private void RenderChips()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (_recording)
        {
            panel.Children.Add(new TextBlock { Text = HotkeyText.RecordingPrompt, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
            _frame.BorderBrush = SettingsRows.SystemBrush("AccentFillColorDefaultBrush");
            _frame.BorderThickness = new Thickness(2);
        }
        else
        {
            var chips = HotkeyText.Chips(Chord);
            if (chips.Count == 0)
                panel.Children.Add(new TextBlock { Text = HotkeyText.NoShortcut, FontSize = 13, Margin = new Thickness(6, 0, 6, 0), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            foreach (var chip in chips)
                panel.Children.Add(Chip(chip));
            if (_notRegistered)
            {
                _frame.BorderBrush = SettingsRows.ThemeBrush(_scope, "HistoryWarningStrokeBrush");
                _frame.BorderThickness = new Thickness(2);
            }
            else
            {
                _frame.BorderBrush = SettingsRows.SystemBrush("ControlStrokeColorDefaultBrush");
                _frame.BorderThickness = new Thickness(1);
            }
        }
        _button.Content = panel;
    }

    private Border Chip(string key) => new()
    {
        Background = SettingsRows.SystemBrush("ControlSolidFillColorDefaultBrush"),
        BorderBrush = SettingsRows.SystemBrush("ControlStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2),
        Child = new TextBlock { Text = key, FontFamily = SettingsRows.MonoFont, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
    };

    private void SetDetail(string text, bool warn)
    {
        _detail.Text = text;
        _detail.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        _detail.Foreground = warn
            ? SettingsRows.ThemeBrush(_scope, "HistoryDangerBrush")
            : SettingsRows.SystemBrush("TextFillColorSecondaryBrush");
    }

    private static VirtualKey? NormalizeModifier(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => VirtualKey.Control,
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => VirtualKey.Shift,
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => VirtualKey.Menu,
        VirtualKey.LeftWindows or VirtualKey.RightWindows => VirtualKey.LeftWindows,
        _ => null,
    };

    private static string? KeyToString(VirtualKey key)
    {
        var k = (int)key;
        if (k is >= (int)VirtualKey.A and <= (int)VirtualKey.Z)
            return ((char)k).ToString();
        if (k is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
            return ((char)k).ToString();
        if (k is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24)
            return "F" + (k - (int)VirtualKey.F1 + 1);

        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Enter => "Enter",
            VirtualKey.Tab => "Tab",
            VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            _ => Enum.IsDefined(key) ? key.ToString() : null,
        };
    }
}
