using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VoiceType.Uno.Presentation.History;

// Placeholder — replaced by agent J at merge. Exists only so navigation compiles.
public sealed partial class RecordingPage : Page
{
    public RecordingPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string recordingId)
            Body.Text = $"Recording {recordingId}";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }
}
