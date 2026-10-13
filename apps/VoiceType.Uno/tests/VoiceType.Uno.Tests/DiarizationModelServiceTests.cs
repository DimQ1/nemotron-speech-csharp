using SpeechLib.Diarization;
using SpeechLib.ModelDownload;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class DiarizationModelCatalogTests
{
    [Fact]
    public void Catalog_ShouldOfferInt8AsRecommendedAndFp32()
    {
        var all = DiarizationModelCatalog.All;

        Assert.Equal(["diarization-int8", "diarization-fp32"], all.Select(m => m.Id));
        Assert.Same(DiarizationModelCatalog.Recommended, all[0]);
        Assert.True(all[0].IsRecommended);
        Assert.False(all[1].IsRecommended);
        Assert.Equal(DiarizationPrecision.Int8, all[0].Precision);
        Assert.Equal(DiarizationPrecision.Fp32, all[1].Precision);
        Assert.All(all, m => Assert.Equal("DimQ1/nemotron-3-diarization-onnx", m.RepoId));
        Assert.Equal(["int8", "fp32"], all.Select(m => m.RepoSubfolder));
    }

    [Fact]
    public void Catalog_ShouldUseTheFolderNamesTheLocatorFinds()
    {
        Assert.Equal(DiarizationModelLocator.Int8FolderName, DiarizationModelCatalog.All[0].Folder);
        Assert.Equal(DiarizationModelLocator.Fp32FolderName, DiarizationModelCatalog.All[1].Folder);
        Assert.Equal("nemotron-3-diarization-onnx-int8", DiarizationModelCatalog.All[0].Folder);
        Assert.Equal("nemotron-3-diarization-onnx-fp32", DiarizationModelCatalog.All[1].Folder);
    }

    [Fact]
    public void Catalog_ShouldCarryThePublishedDownloadSizes()
    {
        Assert.Equal(105_852_942, DiarizationModelCatalog.All[0].SizeBytes);
        Assert.Equal(399_040_397, DiarizationModelCatalog.All[1].SizeBytes);
    }

    [Fact]
    public void Catalog_ShouldGivePhonesOnlyTheInt8Model()
    {
        Assert.Equal([DiarizationModelCatalog.All[0]], DiarizationModelCatalog.ForDevice(isPhone: true));
        Assert.Equal(2, DiarizationModelCatalog.ForDevice(isPhone: false).Count);
    }

    [Fact]
    public void Catalog_ShouldFindByIdAndFolder_AndHaveUniqueDownloadKeys()
    {
        Assert.Same(DiarizationModelCatalog.All[1], DiarizationModelCatalog.Find("DIARIZATION-FP32"));
        Assert.Same(DiarizationModelCatalog.All[0], DiarizationModelCatalog.FindByFolder("nemotron-3-diarization-onnx-int8"));
        Assert.Null(DiarizationModelCatalog.FindByFolder("nope"));
        Assert.Null(DiarizationModelCatalog.Find(null));
        Assert.Equal(["diarization-int8", "diarization-fp32"], DiarizationModelCatalog.All.Select(DiarizationModelCatalog.DownloadKey));
    }

    [Fact]
    public void Catalog_ShouldShowTheLicenseAndNotLeakIntoTheSpeechCatalog()
    {
        Assert.Equal("OpenMDW-1.1", DiarizationModelCatalog.License);
        Assert.Contains("NVIDIA", DiarizationModelCatalog.Attribution);
        Assert.DoesNotContain(ModelCatalog.Models, m => m.RepoId.Contains("diarization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ModelCatalog.OrderedModels, m => m.SubfolderName.Contains("diarization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateRequest_ShouldDownloadTheSubfolderIntoTheDiarizationFolder()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);

        var request = DiarizationModelService.CreateRequest(DiarizationModelCatalog.All[0], layout);

        Assert.Equal("diarization-int8", request.Key);
        Assert.Equal("DimQ1/nemotron-3-diarization-onnx", request.RepoId);
        Assert.Equal("int8", request.Subfolder);
        Assert.Equal(Path.Combine(temp.Path, "diarization", "nemotron-3-diarization-onnx-int8"), request.TargetDirectory);
        Assert.Equal(ModelDownloadKind.Diarization, request.Kind);
        Assert.NotNull(request.AfterDownload);
        Assert.Contains("Speaker detection", request.Title);
    }

    [Fact]
    public void ModelFileSelection_ShouldStripTheRepoSubfolderLikeForParakeet()
    {
        RemoteFile[] files =
        [
            new("int8/diar_step.int8.onnx", "int8/diar_step.int8.onnx", 104_653_656),
            new("int8/diar_frontend.onnx", "int8/diar_frontend.onnx", 1_186_102),
            new("int8/diar_config.json", "int8/diar_config.json", 13_184),
            new("fp32/diar_step.onnx", "fp32/diar_step.onnx", 397_841_111),
            new("README.md", "README.md", 1220),
            new("LICENSE", "LICENSE", 2619),
        ];

        var selected = ModelFileSelection.ForSubfolder(files, "int8");

        Assert.Equal(["diar_step.int8.onnx", "diar_frontend.onnx", "diar_config.json"], selected.Select(f => f.LocalPath));
        Assert.Equal(DiarizationModelLocator.Int8DownloadBytes, selected.Sum(f => f.SizeBytes));
    }
}

public sealed class DiarizationModelServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeHubTransport _transport = new();
    private readonly ModelDownloadManager _downloads;
    private readonly ModelStorageLayout _layout;
    private string? _preferred;

    public DiarizationModelServiceTests()
    {
        _layout = new ModelStorageLayout(Path.Combine(_temp.Path, "models"));
        _downloads = new ModelDownloadManager(new HuggingFaceClient(_transport), progressInterval: TimeSpan.FromMilliseconds(10));
        _transport.AddRepo("DimQ1/nemotron-3-diarization-onnx",
            ("int8/diar_step.int8.onnx", 104_653_656), ("int8/diar_frontend.onnx", 1_186_102), ("int8/diar_config.json", 13_184),
            ("fp32/diar_step.onnx", 397_841_111), ("fp32/diar_frontend.onnx", 1_186_102), ("fp32/diar_config.json", 13_184),
            ("README.md", 1220), ("LICENSE", 2619));
    }

    public void Dispose()
    {
        _downloads.Dispose();
        _temp.Dispose();
    }

    private DiarizationModelService NewService(bool isPhone = false) =>
        new(_downloads, () => _layout, () => _preferred, folder => { _preferred = folder; return Task.CompletedTask; }, isPhone);

    private static DiarizationCatalogModel Int8 => DiarizationModelCatalog.All[0];
    private static DiarizationCatalogModel Fp32 => DiarizationModelCatalog.All[1];

    [Fact]
    public void State_ShouldStartAsNotInstalled()
    {
        using var service = NewService();

        Assert.Equal(DiarizationModelState.NotInstalled, service.StateOf(Int8));
        Assert.Null(service.Active);
        Assert.False(service.IsModelAvailable);
        Assert.False(service.IsDownloading);
        Assert.Null(service.DownloadPercent(Int8));
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task Download_ShouldGoThroughDownloadingToActive_AndValidate()
    {
        using var service = NewService();
        var changes = 0;
        service.Changed += () => Interlocked.Increment(ref changes);
        _transport.Hold = new TaskCompletionSource();

        service.StartDownload(Int8);
        await WaitUntil(() => service.DownloadPercent(Int8) is > 0);

        Assert.Equal(DiarizationModelState.Downloading, service.StateOf(Int8));
        Assert.True(service.IsDownloading);
        Assert.False(service.IsModelAvailable);
        Assert.Contains("%", service.DownloadProgressText(Int8));

        _transport.Hold.SetResult();
        await WaitUntil(() => service.StateOf(Int8) == DiarizationModelState.Active);

        var folder = _layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName);
        Assert.True(service.IsModelAvailable);
        Assert.False(service.IsDownloading);
        Assert.Same(Int8, service.Active);
        Assert.True(changes >= 2);
        DiarizationModelLocator.Validate(folder, DiarizationPrecision.Int8); // does not throw
        Assert.NotNull(DiarizationModelLocator.Find(_layout.DiarizationScanRoots));
        Assert.True(File.Exists(Path.Combine(folder, "NOTICE.txt")));
        Assert.Contains("OpenMDW-1.1", File.ReadAllText(Path.Combine(folder, "NOTICE.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "README.md"))); // only the sub-folder of the repo is downloaded
    }

    [Fact]
    public async Task Download_ShouldNotStartTwice_OrWhenInstalled()
    {
        using var service = NewService();
        _transport.Hold = new TaskCompletionSource();

        service.StartDownload(Int8);
        service.StartDownload(Int8);
        await WaitUntil(() => service.DownloadPercent(Int8) is > 0);
        Assert.Single(_downloads.Jobs);

        _transport.Hold.SetResult();
        await WaitUntil(() => service.StateOf(Int8) == DiarizationModelState.Active);
        service.StartDownload(Int8);
        Assert.Single(_downloads.Jobs);
    }

    [Fact]
    public async Task Cancel_ShouldReturnToNotInstalled()
    {
        using var service = NewService();
        _transport.Hold = new TaskCompletionSource();

        service.StartDownload(Int8);
        await WaitUntil(() => service.DownloadPercent(Int8) is > 0);
        service.CancelDownload(Int8);

        await WaitUntil(() => service.StateOf(Int8) == DiarizationModelState.NotInstalled);
        Assert.False(service.IsModelAvailable);
    }

    [Fact]
    public async Task TruncatedDownload_ShouldFailValidation_AndReportTheError()
    {
        using var service = NewService();
        _transport.Truncate = true; // the hub reports the real sizes, the bytes that arrive are short

        service.StartDownload(Int8);
        await WaitUntil(() => service.LastError is not null);

        Assert.Contains("failed", service.LastError);
        Assert.Contains("incomplete", service.LastError);
        Assert.Equal(DiarizationModelState.NotInstalled, service.StateOf(Int8));
        Assert.False(service.IsModelAvailable);
    }

    [Fact]
    public async Task Select_ShouldSwitchBetweenInt8AndFp32()
    {
        using var service = NewService();
        ModelFixtures.DiarizationModel(_layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName));
        ModelFixtures.DiarizationModel(_layout.DiarizationModelPath(DiarizationModelLocator.Fp32FolderName), fp32: true);
        var changes = 0;
        service.Changed += () => changes++;

        Assert.Equal(DiarizationModelState.Active, service.StateOf(Int8)); // int8 first, like the locator
        Assert.Equal(DiarizationModelState.Installed, service.StateOf(Fp32));

        await service.SelectAsync(Fp32);

        Assert.Equal(DiarizationModelLocator.Fp32FolderName, _preferred);
        Assert.Equal(DiarizationModelState.Active, service.StateOf(Fp32));
        Assert.Equal(DiarizationModelState.Installed, service.StateOf(Int8));
        Assert.Same(Fp32, service.Active);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Select_ShouldRefuseAModelThatIsNotInstalled()
    {
        using var service = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectAsync(Fp32));
    }

    [Fact]
    public async Task Select_ShouldRefuseFp32OnAPhone()
    {
        using var service = NewService(isPhone: true);
        ModelFixtures.DiarizationModel(_layout.DiarizationModelPath(DiarizationModelLocator.Fp32FolderName), fp32: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectAsync(Fp32));
        Assert.Equal([Int8], service.Models);
    }

    [Fact]
    public void Active_ShouldFallBackWhenTheChosenModelIsGone()
    {
        using var service = NewService();
        ModelFixtures.DiarizationModel(_layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName));
        _preferred = DiarizationModelLocator.Fp32FolderName; // chosen earlier, deleted since

        Assert.Same(Int8, service.Active);
    }

    [Fact]
    public void Delete_ShouldRemoveTheFolder()
    {
        using var service = NewService();
        var folder = _layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName);
        ModelFixtures.DiarizationModel(folder);
        var changed = false;
        service.Changed += () => changed = true;

        service.Delete(Int8);

        Assert.False(Directory.Exists(folder));
        Assert.Equal(DiarizationModelState.NotInstalled, service.StateOf(Int8));
        Assert.False(service.IsModelAvailable);
        Assert.True(changed);
    }

    [Fact]
    public void IsModelAvailable_ShouldCountAFolderTheUserCopiedIn()
    {
        using var service = NewService();
        ModelFixtures.DiarizationModel(_layout.DiarizationModelPath(DiarizationModelLocator.FolderName));

        Assert.True(service.IsModelAvailable); // the bare export folder is found by the locator
        Assert.Null(service.Active);            // but it is not one of the catalog cards
    }

    [Fact]
    public void Finalize_ShouldThrowForATruncatedFolder()
    {
        var folder = _layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName);
        Directory.CreateDirectory(folder);
        ModelFixtures.Sparse(Path.Combine(folder, DiarizationModelLocator.Int8StepFileName), 1000);

        Assert.Throws<DiarizationModelException>(() => DiarizationModelService.Finalize(Int8, folder));
    }

    // ── Cards and the Detect speakers row ────────────────────────────────

    [Fact]
    public void Card_ShouldOfferDownload_Cancel_Use_ByState()
    {
        var notInstalled = DiarizationUiCardBuilder.Build(Int8, DiarizationModelState.NotInstalled, null, null);
        Assert.Equal(LlmUiAction.Download, notInstalled.Action);
        Assert.Equal("Download", notInstalled.ActionText);
        Assert.False(notInstalled.ShowProgress);
        Assert.False(notInstalled.CanDelete);
        Assert.Contains(notInstalled.Chips, c => c.Text.StartsWith("★"));

        var downloading = DiarizationUiCardBuilder.Build(Int8, DiarizationModelState.Downloading, 42, "42% · 44 / 106 MB");
        Assert.Equal(LlmUiAction.Cancel, downloading.Action);
        Assert.True(downloading.ShowProgress);
        Assert.Equal(42, downloading.Percent);
        Assert.Equal("42% · 44 / 106 MB", downloading.ProgressText);

        var installed = DiarizationUiCardBuilder.Build(Fp32, DiarizationModelState.Installed, null, null);
        Assert.Equal(LlmUiAction.Use, installed.Action);
        Assert.True(installed.CanDelete);
        Assert.Contains(installed.Chips, c => c.Text == "Desktop only");

        var active = DiarizationUiCardBuilder.Build(Int8, DiarizationModelState.Active, null, null);
        Assert.False(active.ActionVisible);
        Assert.Contains(active.Chips, c => c.Kind == LlmUiChipKind.InUse);
    }

    [Fact]
    public void DetectRow_ShouldOfferOneTapDownload_ThenShowProgress()
    {
        var missing = SpeakerDetectUi.Describe(true, false, false, true, null);
        Assert.Equal(SpeakerDetectKind.NoModel, missing.Kind);
        Assert.Equal("Download", missing.ButtonText);
        Assert.True(missing.ButtonEnabled);
        Assert.Contains("106 MB", missing.Detail);
        Assert.DoesNotContain("Models page", missing.Detail);

        var downloading = SpeakerDetectUi.Describe(true, false, false, true, null, new SpeakerModelDownload(40, "40% · 42 / 106 MB"));
        Assert.Equal(SpeakerDetectKind.DownloadingModel, downloading.Kind);
        Assert.True(downloading.IsVisible);
        Assert.True(downloading.ShowProgress);
        Assert.Equal(0.4, downloading.Progress, 3);
        Assert.Equal("Cancel", downloading.ButtonText);
        Assert.Equal("40% · 42 / 106 MB", downloading.Detail);

        var failed = SpeakerDetectUi.Describe(true, false, false, true, null, null, "Download of X failed: no network.");
        Assert.Equal(SpeakerDetectKind.NoModel, failed.Kind);
        Assert.Contains("no network", failed.Detail);

        // Once installed the normal row returns.
        Assert.Equal(SpeakerDetectKind.Idle, SpeakerDetectUi.Describe(true, true, false, true, null, new SpeakerModelDownload(100, null)).Kind);
    }

    [Fact]
    public void SpeakersModelsUi_ShouldNoLongerSayThatTheModelIsNotPublished()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);

        var place = SpeakersModelsUi.PlaceFilesText(layout);

        Assert.Contains(layout.DiarizationDir, place);
        Assert.DoesNotContain("published", place, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenMDW-1.1", SpeakersModelsUi.Licence);
        Assert.Contains("DimQ1/nemotron-3-diarization-onnx", SpeakersModelsUi.SourceText);
    }

    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>A hub with fixed repos; files are sparse (the Hub-reported length) so 105 MB cost nothing.</summary>
    private sealed class FakeHubTransport : IHubTransport
    {
        private readonly Dictionary<string, List<(string Path, long Size)>> _repos = new();

        public TaskCompletionSource? Hold { get; set; }
        public bool Truncate { get; set; }

        public void AddRepo(string repo, params (string Path, long Size)[] files) => _repos[repo] = [.. files];

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            var repo = url[(url.IndexOf("/api/models/", StringComparison.Ordinal) + "/api/models/".Length)..];
            repo = repo[..repo.IndexOf('?')];
            var siblings = string.Join(",", _repos[repo].Select(f => $"{{\"rfilename\":\"{f.Path}\",\"size\":{f.Size}}}"));
            return Task.FromResult($"{{\"siblings\":[{siblings}]}}");
        }

        public async Task DownloadToFileAsync(string url, string tempPath, long expectedSize, Action<long> onBytes, CancellationToken cancellationToken)
        {
            await using var stream = File.Create(tempPath);
            var half = expectedSize / 2;
            onBytes(half);
            if (Hold is { } hold)
                await hold.Task.WaitAsync(cancellationToken);
            stream.SetLength(Truncate ? Math.Max(1, expectedSize / 100) : expectedSize);
            onBytes(expectedSize - half);
        }
    }
}
