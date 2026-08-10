using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VoiceType.WinUI.Services;

/// <summary>Native executable picker used by command destination settings.</summary>
public static class FileBrowser
{
    public static async Task<string?> PickExecutableAsync(nint ownerHwnd)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Select application",
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, ownerHwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}