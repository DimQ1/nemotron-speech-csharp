using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _settingsFile;

    public SettingsStoreTests() =>
        _settingsFile = Path.Combine(_directory.Path, "settings.json");

    public void Dispose() => _directory.Dispose();

    private SettingsStore CreateStore() => new(new SettingsService(_settingsFile));

    private AppSettings ReadFromDisk() => new SettingsService(_settingsFile).Load();

    [Fact]
    public void Current_ShouldBeTheInstanceEveryMutationReturns()
    {
        var store = CreateStore();
        var before = store.Current;

        var after = store.MutateAsync(s => s.Language = "ru").GetAwaiter().GetResult();

        Assert.Same(before, after);
        Assert.Same(before, store.Current);
    }

    [Fact]
    public void MutateAsync_ShouldPersistTheChange()
    {
        var store = CreateStore();

        store.MutateAsync(s => s.Language = "de").GetAwaiter().GetResult();

        Assert.Equal("de", ReadFromDisk().Language);
    }

    [Fact]
    public void MutateAsync_ShouldKeepEveryConcurrentChange()
    {
        var store = CreateStore();
        Action<AppSettings>[] mutations =
        [
            s => s.Language = "ru",
            s => s.AudioSource = "Loopback",
            s => s.SelectedModel = "model-a",
            s => s.ModelsRootPath = "/models",
            s => s.ToggleHotkey = "Ctrl+Alt+K",
            s => s.TranslationTargetLanguage = "en",
        ];

        Task.WhenAll(mutations.Select(m => store.MutateAsync(m))).GetAwaiter().GetResult();

        var persisted = ReadFromDisk();
        Assert.Equal("ru", persisted.Language);
        Assert.Equal("Loopback", persisted.AudioSource);
        Assert.Equal("model-a", persisted.SelectedModel);
        Assert.Equal("/models", persisted.ModelsRootPath);
        Assert.Equal("Ctrl+Alt+K", persisted.ToggleHotkey);
        Assert.Equal("en", persisted.TranslationTargetLanguage);
    }

    [Fact]
    public void Post_ShouldApplyImmediatelyAndPersistInTheBackground()
    {
        var store = CreateStore();

        store.Post(s => s.Language = "ja");

        // Visible to readers before the file write completes…
        Assert.Equal("ja", store.Current.Language);
        // …and durable shortly after.
        Assert.True(SpinWaitUntil(() => ReadFromDisk().Language == "ja"));
    }

    [Fact]
    public void Post_ShouldReportFailuresThroughTheErrorCallback()
    {
        var store = CreateStore();
        var errors = new List<Exception>();

        store.Post(_ => throw new InvalidOperationException("boom"), errors.Add);

        Assert.True(SpinWaitUntil(() => errors.Count == 1));
        Assert.IsType<InvalidOperationException>(errors[0]);
    }

    [Fact]
    public void ReplaceAsync_ShouldSwapTheLiveInstanceAndPersistIt()
    {
        var store = CreateStore();
        var replacement = store.Snapshot();
        replacement.Language = "fr";

        var current = store.ReplaceAsync(replacement).GetAwaiter().GetResult();

        Assert.Same(replacement, current);
        Assert.Same(replacement, store.Current);
        Assert.Equal("fr", ReadFromDisk().Language);
    }

    [Fact]
    public void Snapshot_ShouldBeIndependentOfTheLiveInstance()
    {
        var store = CreateStore();

        var snapshot = store.Snapshot();
        snapshot.Language = "ko";

        Assert.NotEqual("ko", store.Current.Language);
    }

    [Fact]
    public void SaveAsync_ShouldWriteTheCurrentInstance()
    {
        var store = CreateStore();
        store.Current.AudioSource = "Mix";

        store.SaveAsync().GetAwaiter().GetResult();

        Assert.Equal("Mix", ReadFromDisk().AudioSource);
    }

    [Fact]
    public async Task MutateAsync_ShouldRejectANullMutation()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.MutateAsync(null!));
    }

    private static bool SpinWaitUntil(Func<bool> condition, int attempts = 200)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition())
                return true;

            Thread.Sleep(10);
        }

        return condition();
    }
}
