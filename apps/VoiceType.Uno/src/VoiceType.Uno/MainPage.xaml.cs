using VoiceType.Uno.Presentation;

namespace VoiceType.Uno;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;
}
