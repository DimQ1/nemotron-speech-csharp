using System.Diagnostics;
using VoiceType.Uno.Presentation.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>Opens a folder in the platform file manager (desktop heads only).</summary>
internal static class FolderOpener
{
    public static void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"\"{folder}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Could not open {folder}: {ex.Message}");
        }
    }
}

/// <summary>
/// The "another model storage folder" flow shared by the Settings dialog and the Model storage block: asks whether the models
/// already downloaded should move (on the next start, same drive) or stay, and edits the settings to match.
/// The decision logic is <see cref="ModelStorageMigrator.ApplyRootChoice"/>; the wording is <see cref="ModelStorageText"/>.
/// </summary>
internal static class ModelStorageUi
{
    /// <summary>Picks a folder, asks about the models, applies the settings. False when nothing changed.</summary>
    public static async Task<bool> ChangeFolderAsync(XamlRoot? xamlRoot)
    {
        var picked = await ExportDialog.PickFolderAsync(xamlRoot);
        if (picked is null)
            return false;

        var main = App.Services.GetRequiredService<MainViewModel>();
        var original = main.CreateSettingsSnapshot();
        var built = original.Clone();
        built.ModelsRootPath = picked.Path;
        if (await ChooseAsync(xamlRoot, built, original) == ModelRootChoice.Cancel)
            return false;

        await main.ApplySettingsAsync(built);
        return true;
    }

    /// <summary>
    /// When <paramref name="built"/> names another folder than <paramref name="original"/> and the old one holds models, asks
    /// Move on restart / Keep old location / Cancel and edits <paramref name="built"/> accordingly.
    /// </summary>
    public static async Task<ModelRootChoice> ChooseAsync(XamlRoot? xamlRoot, AppSettings built, AppSettings original)
    {
        var oldLayout = ModelStorageLayout.Resolve(original);
        var summary = await Task.Run(() => ModelStorageScanner.Summarize(oldLayout));
        var choice = ModelRootChoice.Keep;
        if (ModelStorageText.NeedsMoveQuestion(oldLayout.Root, built.ModelsRootPath, summary))
        {
            var newRoot = Path.GetFullPath(built.ModelsRootPath.Trim());
            var sameDrive = ModelStorageText.SameDrive(oldLayout.Root, newRoot);
            choice = await AskAsync(xamlRoot, oldLayout.Root, newRoot, summary, sameDrive);
        }

        ModelStorageMigrator.ApplyRootChoice(built, original, choice);
        return choice;
    }

    private static async Task<ModelRootChoice> AskAsync(XamlRoot? xamlRoot, string oldRoot, string newRoot, ModelStorageSummary summary, bool sameDrive)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = ModelStorageText.MoveQuestionTitle,
                Content = new TextBlock { Text = ModelStorageText.MoveQuestion(oldRoot, newRoot, summary, sameDrive), TextWrapping = TextWrapping.Wrap },
                // Another drive: nothing is copied, so "move" would only be a promise that cannot be kept.
                PrimaryButtonText = sameDrive ? ModelStorageText.MoveButton : "",
                SecondaryButtonText = ModelStorageText.KeepButton,
                CloseButtonText = ModelStorageText.CancelButton,
                DefaultButton = sameDrive ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
            };
            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => ModelRootChoice.MoveOnRestart,
                ContentDialogResult.Secondary => ModelRootChoice.Keep,
                _ => ModelRootChoice.Cancel,
            };
        }
        catch (Exception ex)
        {
            // ContentDialog throws when another dialog is open on the same XamlRoot: keep the old folder rather than guess.
            Console.WriteLine($"[VoiceType.Uno] Move question could not be shown: {ex.Message}");
            return ModelRootChoice.Cancel;
        }
    }
}
