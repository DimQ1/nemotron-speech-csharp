using System.Text.Json;
using System.Text;
using LocalVoiceAssistant.Maui.Core;

namespace LocalVoiceAssistant.Maui;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private bool _pageInitialized;
    private bool _webViewReady;
    private bool _htmlLoaded;

    public MainPage()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.MessageAdded += OnMessageAdded;
    }

    private async void OnPageAppearing(object? sender, EventArgs e)
    {
        if (_pageInitialized)
            return;

        _pageInitialized = true;
        if (!_htmlLoaded)
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("lva.html");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            AssistantWebView.Source = new HtmlWebViewSource
            {
                Html = await reader.ReadToEndAsync(),
                BaseUrl = FileSystem.AppDataDirectory
            };
            _htmlLoaded = true;
        }

        await _viewModel.InitializeAsync();
        await PushModelStateAsync();
    }

    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
            return;

        _webViewReady = true;
        await PushModelStateAsync();
        await PushRuntimeStateAsync();
    }

    private async void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Url) || !e.Url.StartsWith("lva://", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
            return;

        try
        {
            switch (uri.Host.ToLowerInvariant())
            {
                case "send":
                    var query = ParseQuery(uri.Query);
                    if (query.TryGetValue("text", out var text))
                        await _viewModel.SendMessageFromUiAsync(text);
                    break;
                case "mic":
                    await _viewModel.ToggleListeningFromUiAsync();
                    break;
                case "retry":
                    await _viewModel.RetryModelDownloadAsync();
                    break;
                case "cancel":
                    if (_viewModel.IsListening)
                        await _viewModel.ToggleListeningFromUiAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "Закрыть");
        }
    }

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_webViewReady)
            return;

        if (e.PropertyName is nameof(MainViewModel.ModelStatusText)
            or nameof(MainViewModel.Progress)
            or nameof(MainViewModel.IsModelReady)
            or nameof(MainViewModel.IsStartupError)
            or nameof(MainViewModel.StatusText))
        {
            await PushModelStateAsync();
        }

        if (e.PropertyName is nameof(MainViewModel.PetState)
            or nameof(MainViewModel.PetStateText)
            or nameof(MainViewModel.IsListening))
        {
            await PushRuntimeStateAsync();
        }
    }

    private async void OnMessageAdded(object? sender, ConversationEntry entry)
    {
        if (!_webViewReady)
            return;

        await EvaluateAsync(
            $"window.lvaAddMessage({JsonSerializer.Serialize(entry.Role)},{JsonSerializer.Serialize(entry.Text)})");
    }

    private Task PushModelStateAsync()
    {
        var progress = Math.Clamp(_viewModel.Progress, 0, 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var error = _viewModel.IsStartupError ? "true" : "false";
        var ready = _viewModel.IsModelReady ? "true" : "false";
        return EvaluateAsync(
            $"window.lvaSetModel({JsonSerializer.Serialize(_viewModel.ModelStatusText)}," +
            $"{JsonSerializer.Serialize(_viewModel.ModelFileText)}," +
            $"{progress},{error},{ready})");
    }

    private Task PushRuntimeStateAsync()
    {
        var state = _viewModel.PetState;
        var text = _viewModel.PetStateText;
        return EvaluateAsync(
            $"window.lvaSetState({JsonSerializer.Serialize(state)},{JsonSerializer.Serialize(text)});" +
            $"window.lvaSetMic({_viewModel.IsListening.ToString().ToLowerInvariant()})");
    }

    private Task EvaluateAsync(string script) =>
        _webViewReady
            ? AssistantWebView.EvaluateJavaScriptAsync(script)
            : Task.CompletedTask;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            values[key] = value;
        }
        return values;
    }

    protected override void OnDisappearing()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.MessageAdded -= OnMessageAdded;
        _viewModel.Dispose();
        base.OnDisappearing();
    }
}
