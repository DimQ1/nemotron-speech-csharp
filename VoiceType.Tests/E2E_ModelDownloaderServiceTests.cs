using System.IO;
using VoiceType.Services;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// End-to-end tests for ModelDownloaderService.
/// These tests hit the real HuggingFace API — they require network access.
/// Run selectively or as integration tests.
/// </summary>
public sealed class E2E_ModelDownloaderServiceTests : IDisposable
{
    private const string TestRepo = "DimQ1/nemotron-speech-onnx";
    private readonly ModelDownloaderService _service = new();
    private readonly List<string> _statusLog = new();
    private readonly List<DownloadProgress> _progressLog = new();
    private bool _completed;
    private bool _completedOk;
    private string _completedMsg = "";

    public E2E_ModelDownloaderServiceTests()
    {
        _service.StatusChanged += s => _statusLog.Add(s);
        _service.ProgressChanged += p => _progressLog.Add(p);
        _service.Completed += (ok, msg) =>
        {
            _completed = true;
            _completedOk = ok;
            _completedMsg = msg;
        };
    }

    // ── FetchRepoFolders ────────────────────────────────────────

    [Fact]
    public async Task FetchRepoFolders_ShouldReturnFolders()
    {
        var folders = await _service.FetchRepoFolders(TestRepo);

        Assert.NotNull(folders);
        Assert.NotEmpty(folders);
        Assert.Contains(folders, f => f.Name.Contains("cpu") || f.Name.Contains("gpu"));
        Assert.All(folders, f => Assert.True(f.Files.Count > 0, $"Folder {f.Name} has no files"));
    }

    [Fact]
    public async Task FetchRepoFolders_AllFilesHaveNames()
    {
        var folders = await _service.FetchRepoFolders(TestRepo);

        foreach (var f in folders)
        foreach (var file in f.Files)
        {
            Assert.False(string.IsNullOrWhiteSpace(file.Name), $"File in {f.Name} has empty name");
            Assert.False(string.IsNullOrWhiteSpace(file.RelativePath), $"File {file.Name} has empty path");
        }
    }

    [Fact]
    public async Task FetchRepoFolders_EachFolderHasUniqueName()
    {
        var folders = await _service.FetchRepoFolders(TestRepo);
        var names = folders.Select(f => f.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Fact]
    public async Task FetchRepoFolders_HfFolderTotalSizeIsSum()
    {
        var folders = await _service.FetchRepoFolders(TestRepo);

        foreach (var f in folders)
        {
            var expected = f.Files.Sum(x => x.SizeBytes);
            Assert.Equal(expected, f.TotalSize);
        }
    }

    [Fact]
    public async Task FetchRepoFolders_InitialSelectedIsTrue()
    {
        var folders = await _service.FetchRepoFolders(TestRepo);
        Assert.All(folders, f => Assert.True(f.Selected));
    }

    [Fact]
    public async Task FetchRepoFolders_StatusEventsFired()
    {
        _statusLog.Clear();
        await _service.FetchRepoFolders(TestRepo);
        Assert.NotEmpty(_statusLog);
    }

    // ── Download (real) ─────────────────────────────────────────

    [Fact]
    public async Task DownloadSingleFile_CompletesAndFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vt_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Download a small known file — tokenizer.json is usually small
            var url = $"https://huggingface.co/{TestRepo}/resolve/main/cpu/tokenizer.json";
            var dest = Path.Combine(tempDir, "tokenizer.json");
            _statusLog.Clear();

            await _service.DownloadSingleFile(url, dest);

            Assert.True(_completed);
            Assert.True(_completedOk);
            Assert.True(File.Exists(dest));
            Assert.True(new FileInfo(dest).Length > 0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadFromHuggingFace_DownloadsSelectedFiles()
    {
        // Fetch folders and pick just one small folder
        var folders = await _service.FetchRepoFolders(TestRepo);
        // Deselect all
        foreach (var f in folders) f.Selected = false;
        // Select cpu folder (contains tokenizer.json, genai_config.json, etc.)
        var cpuFolder = folders.FirstOrDefault(f => f.Name.Contains("cpu"));
        if (cpuFolder is null) return; // skip if not found

        cpuFolder.Selected = true;

        // Optionally pick only the smallest file
        var smallest = cpuFolder.Files.OrderBy(x => x.SizeBytes).First();
        cpuFolder.Files.Clear();
        cpuFolder.Files.Add(smallest);

        var tempDir = Path.Combine(Path.GetTempPath(), "vt_e2e_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            _statusLog.Clear(); _progressLog.Clear();
            await _service.DownloadFromHuggingFace(TestRepo, new List<HfFolder> { cpuFolder }, tempDir);

            Assert.True(_completed);
            Assert.True(_completedOk);
            Assert.NotEmpty(_progressLog);
            var dest = Path.Combine(tempDir, smallest.RelativePath);
            Assert.True(File.Exists(dest));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Download_CancelStopsEarly()
    {
        var folders = await _service.FetchRepoFolders(TestRepo);
        var cpuFolder = folders.FirstOrDefault(f => f.Name.Contains("cpu"));
        if (cpuFolder is null) return;

        var top2 = cpuFolder.Files.OrderByDescending(x => x.SizeBytes).Take(2).ToList();
        cpuFolder.Files.Clear();
        cpuFolder.Files.AddRange(top2);

        var tempDir = Path.Combine(Path.GetTempPath(), "vt_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Start download then cancel after a short delay
            var downloadTask = _service.DownloadFromHuggingFace(TestRepo, new List<HfFolder> { cpuFolder }, tempDir);
            await Task.Delay(200); // give it a moment to start
            _service.Cancel();
            await downloadTask;

            Assert.True(_completed);
            Assert.False(_completedOk); // should be cancelled
            Assert.Contains(_statusLog, s => s.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ── FormatSize ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(500, "500 B")]
    [InlineData(1_000, "1.0 KB")]
    [InlineData(1_500, "1.5 KB")]
    [InlineData(1_000_000, "1.0 MB")]
    [InlineData(2_500_000, "2.5 MB")]
    [InlineData(1_000_000_000, "1.0 GB")]
    [InlineData(3_500_000_000, "3.5 GB")]
    public void FormatSize_ShouldFormatCorrectly(long bytes, string expected)
    {
        Assert.Equal(expected, ModelDownloaderService.FormatSize(bytes));
    }

    public void Dispose() => _service.Dispose();
}
