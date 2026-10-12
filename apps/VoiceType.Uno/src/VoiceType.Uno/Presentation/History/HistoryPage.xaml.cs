using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VoiceType.Uno.Presentation.History;

// Placeholder — replaced by agent J at merge. Exists only so navigation compiles.
public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        this.InitializeComponent();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }
}
