using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.Localization;

/// <summary>
/// The first-run question "Which interface language?" and the one place that changes the language:
/// <see cref="Apply"/> stores the choice and switches the running app (<see cref="LanguageSwitcher"/> rebuilds the pages).
/// </summary>
public static class LanguagePrompt
{
    /// <summary>Stores <paramref name="code"/> as the interface language and switches to it at once.</summary>
    public static void Apply(SettingsStore store, string code)
    {
        var language = Loc.Find(code) ?? Loc.Languages[0];
        if (store.Current.UiLanguage != language.Code)
            store.Post(s => s.UiLanguage = language.Code,
                ex => Console.WriteLine($"[VoiceType.Uno] Interface language not saved: {ex.Message}"));
        Loc.SetLanguage(language.Code);
    }

#if DEBUG
    /// <summary>
    /// Debug hook for checks without the UI: <c>VOICETYPE_SWITCH_LANGUAGE=ru[,de,...]</c> switches to each language in turn,
    /// one every 6 s after start, exactly as the main-menu picker does (live, no restart).
    /// </summary>
    public static void RunDebugSwitches(Microsoft.UI.Dispatching.DispatcherQueue queue, SettingsStore store)
    {
        var codes = Environment.GetEnvironmentVariable("VOICETYPE_SWITCH_LANGUAGE")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes is not { Length: > 0 })
            return;
        _ = Task.Run(async () =>
        {
            foreach (var code in codes)
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                queue.TryEnqueue(() =>
                {
                    Console.WriteLine($"[VoiceType.Uno] Debug language switch -> {code}");
                    if (code == "nav:settings")
                    {
                        // Control step: an ordinary navigation, logged the same way as a language rebuild.
                        LanguageSwitcher.RootFrame?.Navigate(typeof(SettingsUi.SettingsPage));
                        LanguageSwitcher.LogLater();
                        return;
                    }
                    Apply(store, code);
                });
            }
        });
    }
#endif

    /// <summary>
    /// Asks once, when <c>UiLanguage</c> was never set: the list of languages (each in its own name), the system language
    /// preselected; the dialog texts follow the selection. Closing the dialog keeps the preselected language.
    /// </summary>
    public static void ShowOnFirstRun(Frame frame, SettingsStore store)
    {
        if (!string.IsNullOrEmpty(store.Current.UiLanguage))
            return;

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            frame.Loaded -= OnLoaded;
            _ = ShowAsync(frame.XamlRoot, store);
        }

        if (frame.IsLoaded && frame.XamlRoot is not null)
            _ = ShowAsync(frame.XamlRoot, store);
        else
            frame.Loaded += OnLoaded;
    }

    private static async Task ShowAsync(XamlRoot? root, SettingsStore store)
    {
        if (root is null)
            return;
        var selected = Loc.Current;
        try
        {
            var list = new ListView
            {
                ItemsSource = Loc.Languages,
                SelectionMode = ListViewSelectionMode.Single,
                SelectedItem = selected,
                MaxHeight = 420,
            };
            AutomationProperties.SetAutomationId(list, "UiLanguageList");
            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Content = new StackPanel { Children = { hint, list }, MinWidth = 320 },
                DefaultButton = ContentDialogButton.Primary,
            };

            void Localize(UiLanguage language)
            {
                dialog.Title = Loc.In(language.Code, "Language.Prompt.Title");
                hint.Text = Loc.In(language.Code, "Language.Prompt.Hint");
                dialog.PrimaryButtonText = Loc.In(language.Code, "Language.Prompt.Continue");
                dialog.FlowDirection = language.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            }

            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is UiLanguage language)
                {
                    selected = language;
                    Localize(language);
                }
            };
            list.Loaded += (_, _) => list.ScrollIntoView(selected);
            Localize(selected);

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another dialog was open (one per XamlRoot): keep the preselected language, ask again next start.
            Console.WriteLine($"[VoiceType.Uno] Interface language question not shown: {ex.Message}");
            return;
        }
        Apply(store, selected.Code);
    }
}
