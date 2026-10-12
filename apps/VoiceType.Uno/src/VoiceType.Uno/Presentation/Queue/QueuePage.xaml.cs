using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VoiceType.Uno.Presentation.Queue;

// Placeholder — replaced by agent L at merge. Exists only so navigation compiles.
public sealed partial class QueuePage : Page
{
    public QueuePage()
    {
        this.InitializeComponent();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }
}
