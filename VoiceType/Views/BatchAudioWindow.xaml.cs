using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceType.Views;

public partial class BatchAudioWindow : Window
{
    public BatchAudioWindow()
    {
        try
        {
            InitializeComponent();
            DataContext = new ViewModels.BatchAudioViewModel();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open Batch Audio window:\n{ex.Message}",
                "VoiceType Error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    public ViewModels.BatchAudioViewModel ViewModel => (ViewModels.BatchAudioViewModel)DataContext;

    private void FilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedJob is not null && ViewModel.SelectedJob.Status == "Done")
            ViewModel.ViewFileCommand.Execute(ViewModel.SelectedJob);
    }

    private void FilesListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ViewModel.RemoveSelectedCommand.CanExecute(null))
            ViewModel.RemoveSelectedCommand.Execute(null);
    }

    // Tab switching with visual highlight
    private void SwitchToPlainTab(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 0;
        HighlightTab((System.Windows.Controls.Button)sender);
    }
    private void SwitchToWordsTab(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 1;
        HighlightTab((System.Windows.Controls.Button)sender);
    }
    private void SwitchToSpeakersTab(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 2;
        HighlightTab((System.Windows.Controls.Button)sender);
    }

    private void HighlightTab(System.Windows.Controls.Button active)
    {
        // Find parent StackPanel and reset all buttons
        if (active.Parent is System.Windows.Controls.StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is System.Windows.Controls.Button btn)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)); // BgTertiaryBrush
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)); // FgBrush
                }
            }
        }
        // Highlight active
        active.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)); // AccentBrush
        active.Foreground = new SolidColorBrush(Colors.White);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseViewer_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsViewerVisible = false;
        ViewModel.SelectedJob = null;
    }

    // Context menu
    private void AddFiles_Click(object sender, RoutedEventArgs e) => ViewModel.AddFilesCommand.Execute(null);
    private void AddFolder_Click(object sender, RoutedEventArgs e) => ViewModel.AddFolderCommand.Execute(null);
    private void RemoveSelected_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveSelectedCommand.Execute(null);
    private void ClearAll_Click(object sender, RoutedEventArgs e) => ViewModel.ClearAllCommand.Execute(null);

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
