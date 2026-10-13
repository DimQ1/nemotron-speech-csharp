using System.Text;
using SpeechLib.Llm;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public sealed class LlmModelServiceTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _settingsFile;
    private readonly FakeHostConfiguration _host = new();
    private readonly FakeTransport _transport = new();
    private readonly ModelDownloadManager _downloads;
    private readonly LlmModelInfo _small;
    private readonly LlmModelInfo _large;
    private readonly List<LlmModelInfo> _models;

    public LlmModelServiceTests()
    {
        _settingsFile = Path.Combine(_directory.Path, "settings.json");
        _downloads = new ModelDownloadManager(new HuggingFaceClient(_transport), progressInterval: TimeSpan.FromMilliseconds(10));

        // An absolute FileName makes LocalPath point into the temp directory (Path.Combine drops the models dir).
        _small = Model("small", "small-it.litertlm", 4096, phone: true);
        _large = Model("large", "large-it.litertlm", 8192, phone: false);
        _models = [_small, _large];
    }

    public void Dispose()
    {
        _downloads.Dispose();
        _directory.Dispose();
    }

    private LlmModelInfo Model(string id, string file, int context, bool phone) =>
        new(id, id, "test/" + id, Path.Combine(_directory.Path, file), 1024, context, 4, phone, "tag", "desc");

    private SettingsStore NewStore() => new(new SettingsService(_settingsFile));

    private LlmModelService NewService(SettingsStore? store = null) =>
        new(store ?? NewStore(), _downloads, _host, _models);

    private static void Install(LlmModelInfo model) => File.WriteAllText(model.LocalPath, "model");

    [Fact]
    public void Catalog_ShouldListTheThreeGemmasWithTheDefaultFirst()
    {
        Assert.Equal(["gemma-4-e2b", "gemma-4-e4b", "gemma-4-12b"], LlmModelCatalog.All.Select(m => m.Id));
        Assert.Same(LlmModelCatalog.Default, LlmModelCatalog.All[0]);
        Assert.Equal("gemma-4-E2B-it.litertlm", LlmModelCatalog.Default.FileName); // existing installs keep working
        Assert.Equal(4096, LlmModelCatalog.Default.ContextTokens);
        Assert.All(LlmModelCatalog.All, m => Assert.EndsWith(".litertlm", m.FileName));
        Assert.All(LlmModelCatalog.All, m => Assert.True(m.SizeBytes > 1_000_000_000));
        Assert.All(LlmModelCatalog.All.Skip(1), m => Assert.False(m.PhoneSupported));
        Assert.Equal([LlmModelCatalog.Default], LlmModelCatalog.ForDevice(isPhone: true));
        Assert.Equal(3, LlmModelCatalog.ForDevice(isPhone: false).Count);
        Assert.Same(LlmModelCatalog.All[1], LlmModelCatalog.Find("GEMMA-4-E4B"));
        Assert.Null(LlmModelCatalog.Find("nope"));
        Assert.Null(LlmModelCatalog.Find(null));
    }

    [Fact]
    public void Catalog_ShouldKeepTheLegacyDownloadKeyForTheDefaultOnly()
    {
        Assert.Equal("translation-model", LlmModelCatalog.DownloadKey(LlmModelCatalog.Default));
        Assert.Equal("llm-gemma-4-e4b", LlmModelCatalog.DownloadKey(LlmModelCatalog.All[1]));
        Assert.Equal(LlmModelCatalog.All.Count, LlmModelCatalog.All.Select(LlmModelCatalog.DownloadKey).Distinct().Count());
    }

    [Fact]
    public void Settings_ShouldDefaultToTheDefaultModelAndStayBackwardCompatible()
    {
        Assert.Equal("gemma-4-e2b", new AppSettings().LlmModelId);

        // A settings file written before the setting existed.
        File.WriteAllText(_settingsFile, "{ \"Language\": \"ru\", \"TranslationComputeBackend\": \"gpu\" }");
        var loaded = new SettingsService(_settingsFile).Load();
        Assert.Equal("gemma-4-e2b", loaded.LlmModelId);
        Assert.Equal("gpu", loaded.TranslationComputeBackend);

        loaded.LlmModelId = "gemma-4-12b";
        new SettingsService(_settingsFile).Save(loaded);
        Assert.Equal("gemma-4-12b", new SettingsService(_settingsFile).Load().LlmModelId);
    }

    [Fact]
    public void Active_ShouldFallBackToTheDefaultWhenTheSelectedModelIsNotInstalled()
    {
        Install(_small);
        var store = NewStore();
        store.Current.LlmModelId = "large"; // selected but missing

        var service = NewService(store);

        Assert.Same(_small, service.Active);
        Assert.Equal(LlmModelState.Active, service.StateOf(_small));
        Assert.Equal(LlmModelState.NotInstalled, service.StateOf(_large));
    }

    [Fact]
    public void Active_ShouldBeTheSelectedModelWhenInstalled()
    {
        Install(_small);
        Install(_large);
        var store = NewStore();
        store.Current.LlmModelId = "large";

        var service = NewService(store);

        Assert.Same(_large, service.Active);
        Assert.Equal(LlmModelState.Installed, service.StateOf(_small));
    }

    [Fact]
    public async Task Select_ShouldReconfigureTheHostPersistAndRaiseChanged()
    {
        Install(_small);
        Install(_large);
        var store = NewStore();
        var service = NewService(store);
        var changed = 0;
        service.Changed += () => changed++;

        await service.SelectAsync(_large);

        Assert.Same(_large, service.Active);
        Assert.Equal(LlmModelState.Active, service.StateOf(_large));
        Assert.Equal(LlmModelState.Installed, service.StateOf(_small));
        Assert.Equal(new LlmHostSettings(_large.LocalPath, "cpu", 8192), _host.Last);
        Assert.Equal("large", new SettingsService(_settingsFile).Load().LlmModelId);
        Assert.True(changed >= 1);
    }

    [Fact]
    public async Task Select_ShouldRefuseAModelThatIsNotInstalled()
    {
        Install(_small);
        var service = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectAsync(_large));

        Assert.Same(_small, service.Active);
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task Select_ShouldNotPersistWhenTheHostFails()
    {
        Install(_small);
        Install(_large);
        var store = NewStore();
        var service = NewService(store);
        _host.FailWith = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectAsync(_large));

        Assert.Same(_small, service.Active);
        Assert.Equal("gemma-4-e2b", store.Current.LlmModelId);
    }

    [Fact]
    public async Task SetComputeBackend_ShouldReloadWithTheActiveModelAndPersist()
    {
        Install(_small);
        var service = NewService();
        Assert.Equal("cpu", service.ComputeBackend);

        await service.SetComputeBackendAsync("GPU");

        Assert.Equal("gpu", service.ComputeBackend);
        Assert.Equal(new LlmHostSettings(_small.LocalPath, "gpu", 4096), _host.Last);
        Assert.Equal("gpu", new SettingsService(_settingsFile).Load().TranslationComputeBackend);

        await service.SetComputeBackendAsync("whatever"); // unknown values mean cpu
        Assert.Equal("cpu", service.ComputeBackend);
    }

    [Fact]
    public async Task Select_ShouldKeepTheChosenBackend()
    {
        Install(_small);
        Install(_large);
        var store = NewStore();
        store.Current.TranslationComputeBackend = "gpu";
        var service = NewService(store);

        await service.SelectAsync(_large);

        Assert.Equal("gpu", _host.Last!.Backend);
    }

    [Fact]
    public async Task Delete_ShouldRefuseTheActiveModelAndRemoveOthersWithTheirCaches()
    {
        Install(_small);
        Install(_large);
        File.WriteAllText(_large.LocalPath + "_123_456.xnnpack_cache", "cache");
        File.WriteAllText(_large.LocalPath + ".part", "partial");
        var service = NewService();

        Assert.Throws<InvalidOperationException>(() => service.Delete(_small));
        Assert.True(_small.IsInstalled);

        service.Delete(_large);

        Assert.False(_large.IsInstalled);
        Assert.False(File.Exists(_large.LocalPath + "_123_456.xnnpack_cache"));
        Assert.False(File.Exists(_large.LocalPath + ".part"));
        Assert.Equal(LlmModelState.NotInstalled, service.StateOf(_large));

        // Once another model is active the first one can go.
        Install(_large);
        await service.SelectAsync(_large);
        service.Delete(_small);
        Assert.False(_small.IsInstalled);
    }

    [Fact]
    public async Task Download_ShouldReportProgressThenBecomeInstalled()
    {
        Install(_small);
        var service = NewService();
        _transport.Add("test/large", "large-it.litertlm", 4096);
        _transport.Hold = new TaskCompletionSource();
        var changes = 0;
        service.Changed += () => Interlocked.Increment(ref changes);

        service.StartDownload(_large);
        service.StartDownload(_large); // a second request is the same job

        Assert.Equal(LlmModelState.Downloading, service.StateOf(_large));
        Assert.Single(_downloads.Jobs);
        Assert.Equal("llm-large", _downloads.Jobs[0].Key);
        await WaitUntil(() => service.DownloadPercent(_large) is > 0);
        Assert.InRange(service.DownloadPercent(_large)!.Value, 1, 99);
        Assert.Null(service.DownloadPercent(_small));

        _transport.Hold.SetResult();
        await _downloads.Jobs[0].Completion;
        await WaitUntil(() => service.StateOf(_large) == LlmModelState.Installed);

        Assert.True(_large.IsInstalled);
        Assert.Null(service.DownloadPercent(_large));
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task CancelDownload_ShouldReturnToNotInstalled()
    {
        Install(_small);
        var service = NewService();
        _transport.Add("test/large", "large-it.litertlm", 4096);
        _transport.Hold = new TaskCompletionSource();

        service.StartDownload(_large);
        await WaitUntil(() => service.DownloadPercent(_large) is > 0);
        service.CancelDownload(_large);

        await WaitUntil(() => service.StateOf(_large) == LlmModelState.NotInstalled);
        Assert.False(_large.IsInstalled);
    }

    [Fact]
    public void CreateRequest_ShouldTargetOneFileOfTheRepo()
    {
        var request = LlmModelService.CreateRequest(LlmModelCatalog.All[1]);

        Assert.Equal("litert-community/gemma-4-E4B-it-litert-lm", request.RepoId);
        Assert.Equal("gemma-4-E4B-it.litertlm", request.SingleFile);
        Assert.Equal(ModelStorageLayout.Current.LlmDir, request.TargetDirectory);
        Assert.Equal("llm-gemma-4-e4b", request.Key);
    }

    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeHostConfiguration : ILlmHostConfiguration
    {
        public List<LlmHostSettings> Calls { get; } = [];
        public Exception? FailWith { get; set; }
        public LlmHostSettings? Last => Calls.Count == 0 ? null : Calls[^1];
        public LlmHostSettings Settings => Last ?? new LlmHostSettings("", "cpu", 4096);

        public Task ReconfigureAsync(LlmHostSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailWith is { } failure)
                return Task.FromException(failure);
            Calls.Add(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransport : IHubTransport
    {
        private readonly Dictionary<string, Dictionary<string, int>> _repos = new();

        public TaskCompletionSource? Hold { get; set; }

        public void Add(string repo, string file, int size)
        {
            if (!_repos.TryGetValue(repo, out var files))
                _repos[repo] = files = new();
            files[file] = size;
        }

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            var repo = url[(url.IndexOf("/api/models/", StringComparison.Ordinal) + "/api/models/".Length)..];
            repo = repo[..repo.IndexOf('?')];
            var siblings = string.Join(",", _repos[repo].Select(f => $"{{\"rfilename\":\"{f.Key}\",\"size\":{f.Value}}}"));
            return Task.FromResult($"{{\"siblings\":[{siblings}]}}");
        }

        public async Task DownloadToFileAsync(string url, string tempPath, long expectedSize, Action<long> onBytes, CancellationToken cancellationToken)
        {
            await using var stream = File.Create(tempPath);
            var half = (int)(expectedSize / 2);
            await stream.WriteAsync(new byte[half], cancellationToken);
            onBytes(half);
            if (Hold is { } hold)
                await hold.Task.WaitAsync(cancellationToken);
            await stream.WriteAsync(new byte[expectedSize - half], cancellationToken);
            onBytes(expectedSize - half);
        }
    }
}
