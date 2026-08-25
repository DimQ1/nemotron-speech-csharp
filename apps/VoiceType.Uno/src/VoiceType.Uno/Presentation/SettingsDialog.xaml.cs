using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Text;
using VoiceType.Uno.Services;
using Windows.System;

namespace VoiceType.Uno.Presentation;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsViewModel ViewModel { get; }

    private readonly HashSet<VirtualKey> _pressedModifiers = new();

    public SettingsDialog(AppSettings settings)
    {
        ViewModel = new SettingsViewModel(settings);
        InitializeComponent();
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
        => _pressedModifiers.Clear();

    private void HotkeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        var modifier = NormalizeModifier(e.Key);
        if (modifier is not null)
        {
            _pressedModifiers.Add(modifier.Value);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            box.Text = "";
            _pressedModifiers.Clear();
            e.Handled = true;
            return;
        }

        box.Text = BuildChord(e.Key);
        e.Handled = true;
    }

    private void HotkeyBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        var modifier = NormalizeModifier(e.Key);
        if (modifier is not null)
            _pressedModifiers.Remove(modifier.Value);
    }

    private string BuildChord(VirtualKey key)
    {
        var sb = new StringBuilder();
        if (_pressedModifiers.Contains(VirtualKey.Control)) sb.Append("Ctrl+");
        if (_pressedModifiers.Contains(VirtualKey.Shift)) sb.Append("Shift+");
        if (_pressedModifiers.Contains(VirtualKey.Menu)) sb.Append("Alt+");
        if (_pressedModifiers.Contains(VirtualKey.LeftWindows)) sb.Append("Super+");
        sb.Append(KeyToString(key));
        return sb.ToString();
    }

    private static VirtualKey? NormalizeModifier(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => VirtualKey.Control,
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => VirtualKey.Shift,
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => VirtualKey.Menu,
        VirtualKey.LeftWindows or VirtualKey.RightWindows => VirtualKey.LeftWindows,
        _ => null
    };

    private static string KeyToString(VirtualKey key)
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
            _ => key.ToString()
        };
    }

    private async void DownloadModel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.IsDownloadingModel = true;
        ViewModel.DownloadStatus = "Queued ASR model download...";
        try
        {
            // Route through the shared parallel queue so the aggregate progress
            // shows on the main window and in the Downloads window.
            // The variant comes from the Hugging Face catalog picker in Settings.
            var queue = App.Services.GetRequiredService<DownloadQueueService>();
            var item = queue.EnqueueAsrModel(
                ViewModel.ModelsRootPath,
                onCompleted: modelPath => DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.SelectedModel = Path.GetFileName(modelPath);
                    ViewModel.DownloadStatus = $"Downloaded to {modelPath}";
                    ViewModel.NotifyNativeModelChanged();
                }),
                repoId: ViewModel.SelectedAsrModel.RepoId,
                quantizationFolder: ViewModel.SelectedAsrModel.QuantizationFolder);

            await item.Completion;
        }
        catch (OperationCanceledException)
        {
            ViewModel.DownloadStatus = "Download cancelled.";
        }
        catch (Exception ex)
        {
            ViewModel.DownloadStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            ViewModel.IsDownloadingModel = false;
        }
    }

    private async void DownloadTranslationModel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.IsDownloadingModel = true;
        ViewModel.DownloadStatus = "Queued translation model download...";
        try
        {
            // Route through the shared parallel queue (native .litertlm file).
            var queue = App.Services.GetRequiredService<DownloadQueueService>();
            var item = queue.EnqueueTranslationModel(
                onCompleted: modelPath => DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.DownloadStatus = $"Downloaded to {modelPath}";
                    ViewModel.NotifyNativeModelChanged();
                }));

            await item.Completion;
        }
        catch (OperationCanceledException)
        {
            ViewModel.DownloadStatus = "Download cancelled.";
        }
        catch (Exception ex)
        {
            ViewModel.DownloadStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            ViewModel.IsDownloadingModel = false;
        }
    }
}
