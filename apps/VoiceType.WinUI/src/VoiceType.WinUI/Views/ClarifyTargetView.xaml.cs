using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceType.WinUI.ViewModels;

namespace VoiceType.WinUI.Views;

public sealed partial class ClarifyTargetView : UserControl
{
    public ClarifyTargetViewModel ViewModel { get; }

    public event Action<ClarifyCandidate>? CandidateSelected;

    public ClarifyTargetView(ClarifyTargetViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClarifyCandidate candidate })
            CandidateSelected?.Invoke(candidate);
    }
}