using LVA.Core.Configuration;
using LVA.Core.Events;
using LVA.Orchestration;
using LVA.Plugins.Abstractions;
using LVA.Plugins.Host;
using Xunit;

namespace LVA.Tests.Orchestration;

public class Unit_OrchestrationTests
{
    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _store = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_store.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { _store[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default)
        { _store.Remove(key); return Task.CompletedTask; }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Stub plugin with InternetAccess + web.search capability.</summary>
    private sealed class StubWebPlugin : IAssistantPlugin
    {
        public PluginManifest Manifest { get; } = new("web.search-stub", "1.0",
            new[] { "web.search" }, PluginPermissions.InternetAccess);

        public bool HttpAccessed;
        private IPluginContext? _ctx;

        public Task InitAsync(IPluginContext context, CancellationToken ct)
        { _ctx = context; return Task.CompletedTask; }

        public async Task<ToolResult> ExecuteAsync(ToolInvocation call, IProgress<TaskProgress> progress, CancellationToken ct)
        {
            progress.Report(new TaskProgress(30, "ищу в интернете…"));
            try { _ = _ctx!.HttpFactory.CreateClient(); HttpAccessed = true; } catch { }
            await Task.Delay(10, ct);
            progress.Report(new TaskProgress(100, "обобщаю…"));
            return ToolResult.Ok("Результат поиска", new[] { "https://example.com" });
        }
    }

    /// <summary>Stub plugin WITHOUT InternetAccess that tries to get HttpClient.</summary>
    private sealed class NaughtyPlugin : IAssistantPlugin
    {
        public PluginManifest Manifest { get; } = new("naughty", "1.0",
            new[] { "web.search" }, PluginPermissions.None);

        public bool HttpDenied;
        private IPluginContext? _ctx;

        public Task InitAsync(IPluginContext context, CancellationToken ct)
        { _ctx = context; return Task.CompletedTask; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation call, IProgress<TaskProgress> progress, CancellationToken ct)
        {
            try { _ = _ctx!.HttpFactory.CreateClient(); }
            catch (InvalidOperationException) { HttpDenied = true; }
            return Task.FromResult(ToolResult.Ok("done"));
        }
    }

    private static QuestionReadyEvent Question(string text) => new(
        text, "question", new Dictionary<string, string>(), 1.0, DateTimeOffset.UtcNow);

    private static (Orchestrator, PluginHost, NotificationHub, List<UserNotification>) CreateOrchestrator()
    {
        var host = new PluginHost(new FakeHttpClientFactory(), new FakeSecretStore());
        var notifications = new List<UserNotification>();
        var hub = new NotificationHub();
        hub.RegisterSink(new LambdaSink(n => { notifications.Add(n); return Task.CompletedTask; }));
        var orchestrator = new Orchestrator(host, new ToolRouter(), new TaskTracker(), hub, new PluginOptions());
        return (orchestrator, host, hub, notifications);
    }

    private sealed class LambdaSink : INotificationSink
    {
        private readonly Func<UserNotification, Task> _fn;
        public LambdaSink(Func<UserNotification, Task> fn) => _fn = fn;
        public Task NotifyAsync(UserNotification notification, CancellationToken ct = default) => _fn(notification);
    }

    [Fact]
    public async Task StubPlugin_ShouldCompleteFullLifecycleWithStatuses()
    {
        var (orchestrator, host, _, notifications) = CreateOrchestrator();
        var plugin = new StubWebPlugin();
        await host.RegisterAsync(plugin);

        var result = await orchestrator.HandleQuestionAsync(Question("ассистент, найди новости про .NET"));

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("Результат поиска", result.ResultMarkdown);
        Assert.True(plugin.HttpAccessed, "Plugin with InternetAccess must get HttpClient");

        // DoD: statuses Started → progress → Completed all surfaced
        Assert.Contains(notifications, n => n.Kind == NotificationKind.TaskStarted);
        Assert.Contains(notifications, n => n.Kind == NotificationKind.TaskProgress);
        Assert.Contains(notifications, n => n.Kind == NotificationKind.TaskCompleted);
    }

    [Fact]
    public async Task PluginWithoutInternetAccess_ShouldNotReceiveHttpClient()
    {
        var (orchestrator, host, _, _) = CreateOrchestrator();
        var plugin = new NaughtyPlugin();
        await host.RegisterAsync(plugin);

        var result = await orchestrator.HandleQuestionAsync(Question("найди что-нибудь"));

        Assert.NotNull(result);
        Assert.True(plugin.HttpDenied, "Plugin without InternetAccess must be denied HttpClient");
    }

    [Fact]
    public async Task NoMatchingPlugin_ShouldReturnNull()
    {
        var (orchestrator, _, _, _) = CreateOrchestrator();
        var result = await orchestrator.HandleQuestionAsync(Question("расскажи анекдот"));
        Assert.Null(result);
    }

    [Fact]
    public async Task PluginTimeout_ShouldFailGracefully()
    {
        var (orchestrator, host, _, notifications) = CreateOrchestrator();
        await host.RegisterAsync(new SlowPlugin());
        // tiny timeout for test
        var fastOrchestrator = new Orchestrator(host, new ToolRouter(), new TaskTracker(),
            CreateHub(notifications), new PluginOptions { DefaultTimeoutSec = 1 });

        var result = await fastOrchestrator.HandleQuestionAsync(Question("найди медленные новости"));

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("таймаут", result.Error);
        Assert.Contains(notifications, n => n.Kind == NotificationKind.TaskFailed);
    }

    private static NotificationHub CreateHub(List<UserNotification> sink)
    {
        var hub = new NotificationHub();
        hub.RegisterSink(new LambdaSink(n => { sink.Add(n); return Task.CompletedTask; }));
        return hub;
    }

    private sealed class SlowPlugin : IAssistantPlugin
    {
        public PluginManifest Manifest { get; } = new("slow", "1.0",
            new[] { "web.search" }, PluginPermissions.None);
        public Task InitAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;
        public async Task<ToolResult> ExecuteAsync(ToolInvocation call, IProgress<TaskProgress> progress, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ToolResult.Ok("never");
        }
    }

    [Fact]
    public void RulesRouter_ShouldRouteSearchTriggers()
    {
        var router = new ToolRouter();
        var plugins = new[] { ("websearch", new PluginManifest("websearch", "1.0", new[] { "web.search" }, PluginPermissions.InternetAccess)) };

        Assert.NotNull(router.Route(Question("найди погоду в Минске"), plugins));
        Assert.NotNull(router.Route(Question("what is the latest news"), plugins));
        Assert.Null(router.Route(Question("который час"), plugins.Select(p => (p.Item1, p.Item2))));
    }
}
