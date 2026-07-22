using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Folder picker using Windows.Storage.Pickers.FolderPicker (WinRT).
/// Requires the window HWND for proper modal behaviour.
/// </summary>
public static class FolderBrowser
{
    /// <summary>
    /// Show a folder picker dialog. Returns the selected folder path or null if cancelled.
    /// </summary>
    public static async Task<string?> ShowAsync(string title, string initialPath, nint ownerHwnd)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = title,
        };
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, ownerHwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
