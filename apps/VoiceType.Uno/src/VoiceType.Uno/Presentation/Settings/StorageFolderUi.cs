using Microsoft.UI.Xaml.Automation;
using System.Diagnostics;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>Which layout Settings shows: the phone one on Android (and, in DEBUG, when <c>VOICETYPE_SETTINGS_ANDROID=1</c> asks for a preview on the desktop head).</summary>
internal static class PlatformUi
{
    public static bool IsAndroidLayout
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("VOICETYPE_SETTINGS_ANDROID") == "1")
                return true;
#endif
            return OperatingSystem.IsAndroid();
        }
    }
}

/// <summary>Restarting the app from Settings ("Restart now"): desktop heads only.</summary>
internal static class AppRestart
{
    /// <summary>Android keeps its folders in private storage and never needs a restart for them.</summary>
    public static bool IsAvailable => !PlatformUi.IsAndroidLayout;

    /// <summary>Starts a new process and exits this one. The new one waits for this process to end before it moves any folder.</summary>
    public static void RestartNow()
    {
        try
        {
            var command = RestartCommand.Build(Environment.ProcessPath, Environment.GetCommandLineArgs());
            if (command is null)
            {
                Console.WriteLine("[VoiceType.Uno] Restart: the program path is unknown.");
                return;
            }

            var info = new ProcessStartInfo(command.Value.FileName) { UseShellExecute = false };
            foreach (var argument in command.Value.Arguments)
                info.ArgumentList.Add(argument);
            info.Environment[RestartCommand.ParentPidVariable] = Environment.ProcessId.ToString();
            Process.Start(info);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Restart failed: {ex.Message}");
        }
    }

    /// <summary>At start-up after a restart: waits (up to 15 s) for the previous process, so the moves of folders find no locked file.</summary>
    public static void WaitForPreviousInstance()
    {
        try
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable(RestartCommand.ParentPidVariable), out var pid))
                return;
            Environment.SetEnvironmentVariable(RestartCommand.ParentPidVariable, null);
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit(15_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The previous process is already gone.
        }
    }
}

/// <summary>
/// The shared folder-change flow of Settings › Storage: pick a folder, refuse a folder inside the current one, ask what to do
/// with what is stored (the wording is <see cref="StorageFolderText"/>).
/// </summary>
internal static class StorageFolderUi
{
    /// <summary>Opens the system folder picker; a folder inside the current one (or around it) is refused and asked for again.</summary>
    public static async Task<string?> PickFolderAsync(XamlRoot? xamlRoot, string currentFolder)
    {
        while (true)
        {
            string? path;
#if DEBUG
            // UI checks cannot drive the system folder picker: VOICETYPE_SETTINGS_PICK names the folder it would return.
            if (Environment.GetEnvironmentVariable("VOICETYPE_SETTINGS_PICK") is { Length: > 0 } scripted)
                path = scripted;
            else
#endif
                path = (await ExportDialog.PickFolderAsync(xamlRoot))?.Path;
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (StorageFolderText.Relation(currentFolder, path) != FolderRelation.Nested)
                return path;

            var again = await HistoryDialogs.ShowSafeAsync(new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Choose another folder",
                Content = Parts(new[] { new StorageTextPart(StorageFolderText.NestedWarning) }),
                PrimaryButtonText = StorageFolderText.ChooseAnother,
                CloseButtonText = ModelStorageText.CancelButton,
                DefaultButton = ContentDialogButton.Primary,
            });
            if (again != ContentDialogResult.Primary)
                return null;
        }
    }

    /// <summary>
    /// "Move … to the new folder?" with the buttons the design gives each kind: Move on restart (same drive; app data any drive),
    /// Keep old location (models and recordings), Cancel. The buttons live in the dialog content, stacked on a phone: three labels
    /// do not fit side by side in the dialog's own button row at 360 px.
    /// </summary>
    public static async Task<ModelRootChoice> AskAsync(
        XamlRoot? xamlRoot, StorageFolderKind kind, string subject, string oldFolder, string newFolder, FolderRelation relation)
    {
        var choice = ModelRootChoice.Cancel;
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = StorageFolderText.QuestionTitle(kind) };

        var buttons = new List<(string Label, ModelRootChoice Choice)>();
        if (StorageFolderText.OffersMove(kind, relation))
            buttons.Add((ModelStorageText.MoveButton, ModelRootChoice.MoveOnRestart));
        if (StorageFolderText.OffersKeep(kind, relation))
            buttons.Add((ModelStorageText.KeepButton, ModelRootChoice.Keep));
        buttons.Add((ModelStorageText.CancelButton, ModelRootChoice.Cancel));

        var wide = (xamlRoot?.Size.Width ?? 0) >= 560;
        var column = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, result) = buttons[i];
            var button = new Button
            {
                Style = SettingsRows.StyleOf(i == 0 && result != ModelRootChoice.Cancel ? "HistoryAccentButtonStyle" : "HistoryActionButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                choice = result;
                dialog.Hide();
            };
            if (wide)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(button, i);
                row.Children.Add(button);
            }
            else
            {
                column.Children.Add(button);
            }
        }

        var content = new StackPanel { MaxWidth = 460 };
        content.Children.Add(Parts(StorageFolderText.Question(kind, subject, oldFolder, newFolder, relation)));
        content.Children.Add(wide ? row : column);
        dialog.Content = content;
        await HistoryDialogs.ShowSafeAsync(dialog);
        return choice;
    }

    /// <summary>Text parts as a column: running text wraps, paths are selectable monospace.</summary>
    private static UIElement Parts(IReadOnlyList<StorageTextPart> parts)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
        foreach (var part in parts)
        {
            panel.Children.Add(part.IsPath
                ? SettingsRows.PathBox(part.Text, "Folder path")
                : new TextBlock { Text = part.Text, TextWrapping = TextWrapping.Wrap });
        }
        return panel;
    }
}
