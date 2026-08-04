using System.Net;
using System.Net.Http;
using LocalVoiceAssistant.Maui.Core;
using LocalVoiceAssistant.Maui.Models;
using LocalVoiceAssistant.Maui.Plugins;
using Xunit;

namespace LocalVoiceAssistant.Tests;

public sealed class Unit_LvaRuntimeTests
{
    [Fact]
    public void ActivationGate_ShouldRequireMarkerAndIntent()
    {
        var gate = new ActivationGate();

        var activated = gate.Evaluate("Ассистент, найди расписание поездов");
        var passive = gate.Evaluate("найди расписание поездов");

        Assert.True(activated.IsActivated);
        Assert.True(activated.SemanticScore >= 0.85);
        Assert.Equal("найди расписание поездов", activated.CleanText);
        Assert.False(passive.IsActivated);
    }

    [Fact]
    public async Task PluginHost_ShouldDenyMissingPermission()
    {
        var plugin = new PermissionedPlugin();
        var host = new PluginHost([plugin], []);

        var result = await host.ExecuteAsync(
            new ToolInvocation(plugin.Id, "test", DateTimeOffset.UtcNow),
            null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("permission_denied", result.ErrorCode);
    }

    [Fact]
    public async Task SqliteConversationStore_ShouldPersistAndSearchMessages()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "lva-tests",
            $"{Guid.NewGuid():N}",
            "history.db");
        var store = new SqliteConversationStore(databasePath);
        var entry = new ConversationEntry(
            Guid.NewGuid(),
            "assistant",
            "Результаты поиска по SpeechLib",
            DateTimeOffset.UtcNow,
            "web-search");

        await store.AppendAsync(entry, CancellationToken.None);
        var matches = await store.SearchAsync("SpeechLib", CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(entry.Id, matches[0].Id);
    }

    [Fact]
    public async Task WebSearchPlugin_ShouldParseResultsAndReportProgress()
    {
        const string html = """
            <html><body>
            <a class="result__a" href="https://example.com/one">First result</a>
            <a class="result__snippet">First snippet</a>
            <a class="result__a" href="https://example.com/two">Second result</a>
            <a class="result__snippet">Second snippet</a>
            </body></html>
            """;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(html));
        var plugin = new WebSearchPlugin(new DuckDuckGoSearchProvider(httpClient));
        var progress = new RecordingProgress();

        var result = await plugin.InvokeAsync(
            new ToolInvocation(plugin.Id, "найди SpeechLib", DateTimeOffset.UtcNow),
            progress,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.SearchResults?.Count);
        Assert.Contains("First result", result.Message);
        Assert.Contains(0.1, progress.Values);
        Assert.Equal(1, progress.Values[^1]);
    }

    [Fact]
    public async Task HuggingFaceModelBootstrap_ShouldDownloadAndValidateModelFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "lva-model-tests", Guid.NewGuid().ToString("N"));
        var responses = new Dictionary<string, string>
        {
            ["/api/models/test-owner/test-model"] =
                """{"siblings":[{"rfilename":"genai_config.json","size":16},{"rfilename":"weights/model.onnx","size":7}]}""",
            ["/test-owner/test-model/resolve/main/genai_config.json"] = "{}",
            ["/test-owner/test-model/resolve/main/weights/model.onnx"] = "weights"
        };
        using var httpClient = new HttpClient(new ModelHttpMessageHandler(responses));
        using var service = new HuggingFaceModelBootstrapService(httpClient, "https://huggingface.test");
        var progress = new RecordingModelProgress();

        try
        {
            var result = await service.EnsureModelAsync(
                root,
                new LvaModelDefinition("test-owner/test-model", "Test", "Test", 1),
                progress,
                CancellationToken.None);

            Assert.True(result.IsReady);
            Assert.True(service.IsModelReady(result.ModelPath));
            Assert.Equal("weights", await File.ReadAllTextAsync(
                Path.Combine(result.ModelPath, "weights", "model.onnx")));
            Assert.Equal(1, progress.Values[^1].OverallProgress);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PermissionedPlugin : IAssistantPlugin
    {
        public string Id => "permissioned";
        public string Name => "Permissioned";
        public string Description => "Test plugin";

        public PluginManifest Manifest => new(
            Id,
            Name,
            "1.0.0",
            Description,
            new HashSet<PluginCapability> { PluginCapability.Search },
            new HashSet<PluginPermission> { PluginPermission.Network });

        public bool CanHandle(string utterance) => true;

        public Task<AssistantResponse> ExecuteAsync(
            string utterance,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AssistantResponse("ok"));
    }

    private sealed class RecordingProgress : IProgress<PluginProgress>
    {
        public List<double?> Values { get; } = [];

        public void Report(PluginProgress value) => Values.Add(value.Percent);
    }

    private sealed class RecordingModelProgress : IProgress<ModelDownloadProgress>
    {
        public List<ModelDownloadProgress> Values { get; } = [];

        public void Report(ModelDownloadProgress value) => Values.Add(value);
    }

    private sealed class ModelHttpMessageHandler(
        IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!responses.TryGetValue(request.RequestUri?.AbsolutePath ?? string.Empty, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
    }
}
