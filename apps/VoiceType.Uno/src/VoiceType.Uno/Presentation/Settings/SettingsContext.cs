using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>
/// The app-wide applier for Settings: every change goes through <see cref="DeferredSettingsApplier"/> over
/// <see cref="MainViewModel.ApplySettingsAsync"/>. A compute change made during a recording waits here and is applied when
/// the recording stops, whichever page is open by then.
/// </summary>
internal static class SettingsApplier
{
    private static DeferredSettingsApplier? s_instance;

    public static DeferredSettingsApplier Instance => s_instance ??= Create();

    private static DeferredSettingsApplier Create()
    {
        var main = App.Services.GetRequiredService<MainViewModel>();
        var applier = new DeferredSettingsApplier(main.ApplySettingsAsync, () => main.IsRecording, main.CreateSettingsSnapshot);
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRecording) && !main.IsRecording)
                _ = FlushAsync(applier);
        };
        return applier;
    }

    private static async Task FlushAsync(DeferredSettingsApplier applier)
    {
        try
        {
            await applier.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Held-back settings could not be applied: {ex.Message}");
        }
    }
}

/// <summary>What a section needs from the page: the live settings, the apply path, navigation and the dialog root.</summary>
internal sealed class SettingsContext
{
    public SettingsContext(FrameworkElement host, Action<Type, object?> navigate, Action refreshHub)
    {
        Host = host;
        Navigate = navigate;
        RefreshHub = refreshHub;
        Main = App.Services.GetRequiredService<MainViewModel>();
        Store = App.Services.GetRequiredService<SettingsStore>();
    }

    public FrameworkElement Host { get; }

    public MainViewModel Main { get; }

    public SettingsStore Store { get; }

    /// <summary>Opens another page in the app frame (Models, Settings › History).</summary>
    public Action<Type, object?> Navigate { get; }

    /// <summary>Asks the page to refresh the hub subtitles (a value they show changed).</summary>
    public Action RefreshHub { get; }

    public XamlRoot? XamlRoot => Host.XamlRoot;

    public bool Android => PlatformUi.IsAndroidLayout;

    /// <summary>The settings in effect now (read-only: change them through <see cref="ChangeAsync"/>).</summary>
    public AppSettings Live => Store.Current;

    public DeferredSettingsApplier Applier => SettingsApplier.Instance;

    /// <summary>Applies one edit at once; a failure is reported on the status line instead of vanishing in an async void.</summary>
    public async Task ChangeAsync(Action<AppSettings> mutate)
    {
        try
        {
            await Applier.ChangeAsync(mutate);
            RefreshHub();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Setting could not be applied: {ex}");
        }
    }

    /// <summary>Fire-and-forget form for event handlers.</summary>
    public void Change(Action<AppSettings> mutate) => _ = ChangeAsync(mutate);
}
