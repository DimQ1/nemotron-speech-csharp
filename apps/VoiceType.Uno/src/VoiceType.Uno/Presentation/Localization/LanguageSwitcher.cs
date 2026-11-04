using System.Globalization;
using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Presentation.Localization;

/// <summary>
/// A page that holds subscriptions to long-lived objects (the singleton view model, platform events) outside its
/// Loaded/Unloaded pair, so a cached page can be dropped when the language changes. <see cref="Retire"/> detaches them.
/// </summary>
public interface IRetirablePage
{
    void Retire();
}

/// <summary>
/// Applies the interface language to the window without a restart: on <see cref="Loc.LanguageChanged"/> the root frame
/// navigates again to the same pages with the same parameters (the back stack included, so the user stays where they were),
/// and the shell flows right to left for Arabic. Long-lived view models re-compute their own texts on the event.
/// </summary>
public static class LanguageSwitcher
{
    private static Grid? _shell;
    private static Frame? _frame;

    /// <summary>The frame the app navigates in.</summary>
    public static Frame? RootFrame => _frame;

    /// <summary>Wires the shell (the root grid of the window) and its frame. Called once at launch.</summary>
    public static void Attach(Grid shell, Frame frame)
    {
        _shell = shell;
        _frame = frame;
        Track(frame);
        ApplyFlowDirection();
        Loc.LanguageChanged -= OnLanguageChanged;
        Loc.LanguageChanged += OnLanguageChanged;
#if DEBUG
        if (Environment.GetEnvironmentVariable("VOICETYPE_SWITCH_LANGUAGE") is { Length: > 0 })
            _ = Task.Delay(4000).ContinueWith(_ => frame.DispatcherQueue.TryEnqueue(() => LogTree(frame)));
#endif
    }

    /// <summary>
    /// The language the app starts in: the stored choice, or, before the first-run question was answered, the system
    /// language when the app has it (English otherwise).
    /// </summary>
    public static UiLanguage StartLanguage(string? stored) =>
        Loc.Find(stored) ?? Loc.MatchSystem(CultureInfo.CurrentUICulture);

    private static void OnLanguageChanged()
    {
        var shell = _shell;
        if (shell is null)
            return;
        // Always posted: the change usually comes from a control of the page that is about to be replaced.
        if (shell.DispatcherQueue is { } queue)
            queue.TryEnqueue(Rebuild);
        else
            Rebuild();
    }

    private static void ApplyFlowDirection()
    {
        if (_shell is not null)
            _shell.FlowDirection = Loc.Current.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private static void Rebuild()
    {
        if (_shell is null || _frame is null)
            return;
        ApplyFlowDirection();

        // The same (loaded) frame navigates again: a new Frame swapped into the shell was never loaded on Skia.
        var frame = _frame;
        var stack = frame.BackStack.Select(e => (e.SourcePageType, e.Parameter)).ToList();
        if (frame.Content is Page current)
            stack.Add((current.GetType(), CurrentParameter(frame)));
        if (stack.Count == 0)
            stack.Add((typeof(MainPage), null));

        // Cached pages (NavigationCacheMode) would come back in the old language: drop them from the cache first.
        var retiring = RetirablePages.ToList();
        foreach (var page in retiring)
            page.NavigationCacheMode = NavigationCacheMode.Disabled;
        if (frame.Content is Page shown)
            shown.NavigationCacheMode = NavigationCacheMode.Disabled;
        var cacheSize = frame.CacheSize;
        frame.CacheSize = 0;

        frame.BackStack.Clear();
        foreach (var (type, parameter) in stack)
        {
            try
            {
                frame.Navigate(type, parameter);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Language switch could not reopen {type.Name}: {ex.Message}");
            }
        }
        // The first navigation pushed the page that was shown before the switch: not part of the rebuilt stack.
        if (frame.BackStack.Count >= stack.Count)
            frame.BackStack.RemoveAt(0);
        frame.CacheSize = cacheSize;

        foreach (var page in retiring)
        {
            RetirablePages.Remove(page);
            ((IRetirablePage)page).Retire();
        }
#if DEBUG
        if (Environment.GetEnvironmentVariable("VOICETYPE_SWITCH_LANGUAGE") is { Length: > 0 })
            LogLater();
#endif
    }

    // The Frame keeps parameters in its back/forward entries only; the one of the current page is recorded here.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, NavigationParameter> Parameters = new();

    private static void Track(Frame frame) =>
        frame.Navigated += (_, e) =>
        {
            if (e.Content is not null)
                Parameters.AddOrUpdate(e.Content, new NavigationParameter(e.Parameter));
        };

    private static object? CurrentParameter(Frame frame) =>
        frame.Content is { } page && Parameters.TryGetValue(page, out var p) ? p.Value : null;

    // Pages kept by NavigationCacheMode are not reachable from the Frame's public API: they register here.
    private static readonly List<Page> RetirablePages = [];

    /// <summary>Called by a cached page (MainPage) when it is created, so a language switch can retire it.</summary>
    public static void Register(Page page)
    {
        if (page is IRetirablePage && !RetirablePages.Contains(page))
            RetirablePages.Add(page);
    }

#if DEBUG
    /// <summary>Logs what the frame shows 4 s from now (debug checks).</summary>
    public static void LogLater()
    {
        if (_frame is { } frame)
            _ = Task.Delay(4000).ContinueWith(_ => frame.DispatcherQueue.TryEnqueue(() => LogTree(frame)));
    }

    // Check hook for VOICETYPE_SWITCH_LANGUAGE runs: what the rebuilt page shows, without screenshots.
    private static void LogTree(Frame frame)
    {
        var texts = new List<string>();
        void Walk(DependencyObject node)
        {
            if (texts.Count >= 8) return;
            if (node is TextBlock t && !string.IsNullOrWhiteSpace(t.Text))
                texts.Add(t.ActualWidth > 0 ? t.Text : "(unmeasured) " + t.Text);
            for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
        }
        Walk(frame);
        var line = ($"[VoiceType.Uno] Rebuilt in {Loc.Current.Code}: page={frame.Content?.GetType().Name}#{frame.Content?.GetHashCode()} width={frame.ActualWidth:0} shell={_shell?.ActualWidth:0} children={_shell?.Children.Count} flow={_shell?.FlowDirection} texts=[{string.Join(" | ", texts)}] back={frame.BackStack.Count} pageLoaded={(frame.Content as FrameworkElement)?.IsLoaded} pageContent={(frame.Content as Page)?.Content?.GetType().Name} frameKids={Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(frame)} pageKids={(frame.Content is DependencyObject d ? Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d) : -1)} frameLoaded={frame.IsLoaded} inShell={_shell?.Children.Contains(frame)}");
        Console.WriteLine(line);
        // UTF-8 copy: the console code page mangles Cyrillic, Arabic and CJK.
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "voicetype-language-check.log"), line + Environment.NewLine);
    }
#endif

    private sealed record NavigationParameter(object? Value);
}
