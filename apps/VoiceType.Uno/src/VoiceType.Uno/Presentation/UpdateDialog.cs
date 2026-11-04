using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Updates;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// "VoiceType X is available": the release notes and one button for what this installation can do
/// (install and restart / download the package / open the release page), plus Skip and Later.
/// </summary>
public static class UpdateDialog
{
    public static async Task ShowAsync(XamlRoot root, UpdateService updates, UpdatePlan plan, Func<bool>? isRecording = null)
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        var notes = new TextBlock
        {
            Text = AppUpdates.ShortNotes(plan.Release.Notes) is { Length: > 0 } n ? n : Loc.T("Updates.Dialog.NoNotes"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 13,
        };
        var intro = new TextBlock
        {
            Text = Loc.F(plan.Kind switch
            {
                UpdateInstallKind.ReplaceInPlace => "Updates.Dialog.IntroReplace",
                UpdateInstallKind.OpenPackage => "Updates.Dialog.IntroPackage",
                _ when UpdateService.Platform == UpdatePlatform.Android => "Updates.Dialog.IntroAndroid",
                _ => "Updates.Dialog.IntroPage",
            }, UpdateService.CurrentVersion),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.F("Updates.Dialog.Title", plan.Release.Version),
            Content = new StackPanel
            {
                MinWidth = 320,
                Children =
                {
                    intro,
                    new ScrollViewer { MaxHeight = 280, Content = notes },
                    progress,
                    status,
                },
            },
            PrimaryButtonText = Loc.T(plan.Kind switch
            {
                UpdateInstallKind.ReplaceInPlace => "Updates.Dialog.Install",
                UpdateInstallKind.OpenPackage => "Updates.Dialog.Download",
                _ => "Updates.Dialog.OpenPage",
            }),
            SecondaryButtonText = Loc.T("Updates.Dialog.Skip"),
            CloseButtonText = Loc.T("Updates.Dialog.Later"),
            DefaultButton = ContentDialogButton.Primary,
        };

        using var cancel = new CancellationTokenSource();
        var installing = false;
        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            if (installing)
            {
                args.Cancel = true;
                return;
            }
            if (plan.Kind == UpdateInstallKind.ReplaceInPlace && isRecording?.Invoke() == true)
            {
                args.Cancel = true;
                status.Text = Loc.T("Updates.Dialog.StopRecordingFirst");
                status.Visibility = Visibility.Visible;
                return;
            }
            if (plan.Kind == UpdateInstallKind.OpenPage)
            {
                _ = updates.InstallAsync(plan, null, CancellationToken.None);
                return;
            }

            // Keep the dialog open with the progress until the download is done.
            args.Cancel = true;
            var deferral = args.GetDeferral();
            installing = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            status.Visibility = Visibility.Visible;
            status.Text = Loc.T("Updates.Dialog.Downloading");
            var queue = dialog.DispatcherQueue;
            var report = new Progress<double>(v => queue.TryEnqueue(() =>
            {
                progress.Value = v;
                status.Text = Loc.F("Updates.Dialog.DownloadingPercent", v);
            }));
            try
            {
                var quit = await updates.InstallAsync(plan, report, cancel.Token);
                deferral.Complete();
                if (quit)
                {
                    status.Text = Loc.T("Updates.Dialog.Restarting");
                    await Task.Delay(400);
                    Application.Current.Exit();
                    return;
                }
                dialog.Hide();
            }
            catch (OperationCanceledException)
            {
                deferral.Complete();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Update failed: {ex}");
                status.Text = Loc.F("Updates.Dialog.Failed", ex.Message);
                progress.Visibility = Visibility.Collapsed;
                installing = false;
                dialog.IsPrimaryButtonEnabled = true;
                dialog.IsSecondaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        dialog.SecondaryButtonClick += (_, _) => updates.Skip(plan);
        dialog.CloseButtonClick += (_, _) => cancel.Cancel();

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // One dialog per XamlRoot: another one was open.
            Console.WriteLine($"[VoiceType.Uno] Update dialog not shown: {ex.Message}");
        }
    }
}
