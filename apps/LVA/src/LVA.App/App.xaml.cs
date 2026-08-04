namespace LVA.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = _services.GetRequiredService<Views.MainPage>();
        return new Window(page)
        {
            Title = "LVA Assistant",
            Width = 900,
            Height = 700
        };
    }
}
