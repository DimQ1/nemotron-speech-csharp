using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

public enum DeleteOutcome
{
    Cancelled,
    /// <summary>Confirmed in the dialog: deleted at once.</summary>
    Deleted,
    /// <summary>"Ask before deleting" is off: hidden now, committed after the Undo window.</summary>
    Pending,
}

/// <summary>
/// Centred dialogs of the History screens (design §4.7): delete confirmation with
/// "Don't ask again", rename. Built in code: they are small and shared by both pages.
/// </summary>
internal static class HistoryDialogs
{
    /// <summary>
    /// Deletes recordings after the confirmation rule: with "Ask before deleting" on, a
    /// dialog (with "Don't ask again") and an immediate delete; with it off, a soft delete
    /// with an 8 s Undo snackbar.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteRecordingsAsync(
        XamlRoot? xamlRoot, IReadOnlyList<string> ids, long audioBytes, int chatCount)
    {
        if (ids.Count == 0)
            return DeleteOutcome.Cancelled;

        if (!HistoryServices.HistorySettings.ConfirmDelete)
        {
            var list = ids.ToList();
            HistoryServices.Deletes.Schedule(HistoryUiDeleteText.Snackbar(list.Count), list, null,
                () => HistoryServices.DeleteRecordingsNow(list));
            return DeleteOutcome.Pending;
        }

        var text = HistoryUiDeleteText.Recordings(ids.Count, audioBytes, chatCount);
        if (!await ConfirmDeleteAsync(xamlRoot, text, allowDontAsk: true))
            return DeleteOutcome.Cancelled;

        var copy = ids.ToList();
        await Task.Run(() => HistoryServices.DeleteRecordingsNow(copy));
        return DeleteOutcome.Deleted;
    }

    /// <summary>
    /// Shows a destructive confirmation. With <paramref name="allowDontAsk"/>, a checked
    /// "Don't ask again" turns <c>History.ConfirmDelete</c> off (Settings › History turns it back on).
    /// </summary>
    public static async Task<bool> ConfirmDeleteAsync(XamlRoot? xamlRoot, HistoryUiConfirmation text, bool allowDontAsk)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 440 };
        body.Children.Add(new TextBlock { Text = text.Body, TextWrapping = TextWrapping.Wrap });

        CheckBox? dontAsk = null;
        if (allowDontAsk)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = "Don't ask again", TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock
            {
                Text = "Turn it back on in Settings › History",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            dontAsk = new CheckBox { Content = label, MinHeight = 44 };
            AutomationProperties.SetName(dontAsk, "Don't ask again");
            body.Children.Add(dontAsk);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = text.Title,
            Content = body,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = DangerButtonStyle(),
        };

        if (await ShowSafeAsync(dialog) != ContentDialogResult.Primary)
            return false;

        if (dontAsk?.IsChecked == true)
            HistoryServices.UpdateHistorySettings(h => h.ConfirmDelete = false);
        return true;
    }

    /// <summary>
    /// Rename sheet: the user title (empty = back to the generated / first-words title).
    /// Returns null when cancelled.
    /// </summary>
    public static async Task<string?> RenameAsync(XamlRoot? xamlRoot, RecordingManifest manifest)
    {
        var box = new TextBox
        {
            Text = manifest.Title ?? manifest.EffectiveTitle,
            PlaceholderText = RecordingTitle.FromText(manifest.Current?.Excerpt) ?? "Title",
            MinHeight = 44,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(box, "Title");

        var body = new StackPanel { Spacing = 12, MaxWidth = 440 };
        body.Children.Add(box);
        var watchCancel = new CancellationTokenSource();
        string? generated = null;
        body.Children.Add(BuildGemmaTitleRow(manifest, box, watchCancel.Token, title => generated = title));

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Rename recording",
            Content = body,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                dialog.Tag = "enter";
                dialog.Hide();
            }
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        var result = await ShowSafeAsync(dialog);
        watchCancel.Cancel();
        if (result != ContentDialogResult.Primary && !Equals(dialog.Tag, "enter"))
            return null;

        var title = box.Text.Trim();
        // Saving the effective title unchanged keeps the title unset.
        return manifest.Title is null && (title == manifest.EffectiveTitle || title == generated) ? null : title;
    }

    /// <summary>
    /// "✦ Generated by Gemma from v2. Your own title is never replaced." with a Regenerate button (design §4.7).
    /// Regenerate queues a Background <c>autoTitle</c> job; the row then says the title is queued and, when it is
    /// written, puts it into the field and shows the "Generated by Gemma" hint again.
    /// </summary>
    private static UIElement BuildGemmaTitleRow(RecordingManifest manifest, TextBox box, CancellationToken cancellation, Action<string> onGenerated)
    {
        var recordingId = manifest.Id;
        var gemma = Services.Jobs.GemmaAvailability.IsInstalled;
        var queued = HistoryLlmJobs.FindUnfinished(HistoryServices.Jobs.Jobs, recordingId, HistoryJobKind.AutoTitle, _ => true) is not null;
        var knownGeneratedAt = manifest.AutoTitleInfo?.GeneratedAt;

        var hint = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = manifest.AutoTitle is { Length: > 0 } ? "Regenerate" : "Generate", MinHeight = 44, MinWidth = 44, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, "Regenerate title with Gemma");

        void Describe(RecordingManifest m, bool isQueued)
        {
            hint.Text = isQueued
                ? "✦ Title queued: Gemma writes it in the background and it appears here."
                : m.AutoTitle is { Length: > 0 }
                    ? $"✦ Generated by Gemma{(m.AutoTitleInfo?.SourceVersion is { } v ? " from " + v : "")}. Your own title is never replaced; leave the field empty to use it."
                    : gemma
                        ? "Leave the field empty to use the first words of the transcript, or let Gemma write a title."
                        : "Leave the field empty to go back to the first words of the transcript.";
            button.IsEnabled = gemma && !isQueued;
            button.Visibility = gemma ? Visibility.Visible : Visibility.Collapsed;
        }

        Describe(manifest, queued);

        async Task WatchAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(700, cancellation);
                    var current = await Task.Run(() => HistoryServices.Recordings.Get(recordingId)?.Manifest);
                    var pending = HistoryLlmJobs.FindUnfinished(HistoryServices.Jobs.Jobs, recordingId, HistoryJobKind.AutoTitle, _ => true) is not null;
                    if (current is null)
                        return;
                    if (current.AutoTitleInfo?.GeneratedAt != knownGeneratedAt && !string.IsNullOrWhiteSpace(current.AutoTitle))
                    {
                        knownGeneratedAt = current.AutoTitleInfo?.GeneratedAt;
                        box.Text = current.AutoTitle;
                        onGenerated(current.AutoTitle);
                        box.SelectAll();
                        Describe(current, false);
                        return;
                    }
                    if (!pending)
                    {
                        Describe(current, false); // finished without a title (model gone, empty transcript)
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        button.Click += (_, _) =>
        {
            try
            {
                HistoryLlmJobs.EnqueueAutoTitle(HistoryServices.Jobs, recordingId, regenerate: true);
                Describe(manifest, true);
                _ = WatchAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Title job was not queued: {ex.Message}");
            }
        };
        if (queued)
            _ = WatchAsync();

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(hint);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    /// <summary>A plain message with one button.</summary>
    public static Task ShowMessageAsync(XamlRoot? xamlRoot, string title, string message) =>
        ShowSafeAsync(new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 440 },
            CloseButtonText = "OK",
        });

    /// <summary>Yes/no question; true for the primary button.</summary>
    public static async Task<bool> AskAsync(XamlRoot? xamlRoot, string title, string message, string primary, string close = "Cancel") =>
        await ShowSafeAsync(new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
        }) == ContentDialogResult.Primary;

    /// <summary>ShowAsync that reports instead of throwing when another dialog is open on the same root.</summary>
    public static async Task<ContentDialogResult> ShowSafeAsync(ContentDialog dialog)
    {
        ApplyTouchTargets(dialog);
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Dialog '{dialog.Title}' could not be shown: {ex.Message}");
            return ContentDialogResult.None;
        }
    }

    /// <summary>
    /// The ContentDialog buttons are 32 px tall by default; the design asks for 44 px
    /// targets everywhere, so every dialog button gets a 44 px minimum height.
    /// </summary>
    public static void ApplyTouchTargets(ContentDialog dialog)
    {
        // The template restyles the default button with the 32 px AccentButtonStyle, which
        // would undo the 44 px style; the accent look is applied here instead.
        var primaryIsDefault = dialog.DefaultButton == ContentDialogButton.Primary;
        dialog.DefaultButton = ContentDialogButton.None;
        dialog.PrimaryButtonStyle ??= TallButtonStyle(primaryIsDefault ? "AccentButtonStyle" : "DefaultButtonStyle");
        dialog.SecondaryButtonStyle ??= TallButtonStyle("DefaultButtonStyle");
        dialog.CloseButtonStyle ??= TallButtonStyle("DefaultButtonStyle");
    }

    private static Style TallButtonStyle(string basedOnKey)
    {
        var style = new Style(typeof(Button));
        if (Application.Current.Resources.TryGetValue(basedOnKey, out var basedOn) && basedOn is Style b)
            style.BasedOn = b;
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 44d));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    /// <summary>Red primary button for destructive confirmations.</summary>
    public static Style DangerButtonStyle()
    {
        var style = new Style(typeof(Button));
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style basedOn)
            style.BasedOn = basedOn;
        var red = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
        style.Setters.Add(new Setter(Control.BackgroundProperty, red));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, red));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Microsoft.UI.Colors.White)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 44d));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }
}
