using VoiceType.Services;
using VoiceType.ViewModels;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// Unit tests for ModelDownloaderViewModel — testable parts that don't need WPF dispatcher.
/// </summary>
public sealed class Unit_ModelDownloaderViewModelTests
{
    // ── ParseRepoId ─────────────────────────────────────────────

    [Theory]
    [InlineData("DimQ1/nemotron-speech-onnx", "DimQ1/nemotron-speech-onnx")]
    [InlineData("DimQ1/nemotron-speech-onnx/", "DimQ1/nemotron-speech-onnx")]
    [InlineData("https://huggingface.co/DimQ1/nemotron-speech-onnx", "DimQ1/nemotron-speech-onnx")]
    [InlineData("https://huggingface.co/DimQ1/nemotron-speech-onnx/", "DimQ1/nemotron-speech-onnx")]
    [InlineData("huggingface.co/DimQ1/nemotron-speech-onnx", "DimQ1/nemotron-speech-onnx")]
    [InlineData("https://huggingface.co/DimQ1/nemotron-speech-onnx/tree/main/cpu", "DimQ1/nemotron-speech-onnx")]
    [InlineData("  DimQ1/nemotron-speech-onnx  ", "DimQ1/nemotron-speech-onnx")]
    public void ParseRepoId_ShouldReturnBaseRepoId(string input, string expected)
    {
        Assert.Equal(expected, ModelDownloaderViewModel.ParseRepoId(input));
    }

    [Fact]
    public void ParseRepoId_LongSlug_ReturnsFirstTwoSegments()
    {
        Assert.Equal("org/my-repo", ModelDownloaderViewModel.ParseRepoId("org/my-repo/something/else"));
    }

    // ── HfFolder model ──────────────────────────────────────────

    [Fact]
    public void HfFolder_TotalSize_ReturnsSumOfFiles()
    {
        var folder = new HfFolder
        {
            Files = new List<HfFile>
            {
                new() { SizeBytes = 100 },
                new() { SizeBytes = 200 },
                new() { SizeBytes = 300 }
            }
        };
        Assert.Equal(600, folder.TotalSize);
    }

    [Fact]
    public void HfFolder_SizeDisplay_UsesFormatSize()
    {
        var folder = new HfFolder { Files = new List<HfFile> { new() { SizeBytes = 1_500_000 } } };
        Assert.Equal("1.5 MB", folder.SizeDisplay);
    }

    [Fact]
    public void HfFolder_EmptyFiles_HasZeroSize()
    {
        var folder = new HfFolder();
        Assert.Equal(0, folder.TotalSize);
        Assert.Equal("0 B", folder.SizeDisplay);
    }

    // ── DownloadProgress model ──────────────────────────────────

    [Fact]
    public void DownloadProgress_HoldsValues()
    {
        var p = new DownloadProgress
        {
            CurrentFile = "cpu/tokenizer.json",
            FileProgress = 75.5,
            OverallProgress = 42.0,
            DownloadedFiles = 3,
            TotalFiles = 7
        };
        Assert.Equal("cpu/tokenizer.json", p.CurrentFile);
        Assert.Equal(75.5, p.FileProgress);
        Assert.Equal(42.0, p.OverallProgress);
        Assert.Equal(3, p.DownloadedFiles);
        Assert.Equal(7, p.TotalFiles);
    }

    // ── AsyncRelayCommand ───────────────────────────────────────

    [Fact]
    public void AsyncRelayCommand_CanExecute_WhenNotRunning()
    {
        var cmd = new AsyncRelayCommand(() => Task.CompletedTask, () => true);
        Assert.True(cmd.CanExecute(null));
    }

    [Fact]
    public void AsyncRelayCommand_CannotExecute_WhenCanExecuteFalse()
    {
        var cmd = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        Assert.False(cmd.CanExecute(null));
    }

    [Fact]
    public async Task AsyncRelayCommand_ExecutesTask()
    {
        var tcs = new TaskCompletionSource<bool>();
        var cmd = new AsyncRelayCommand(async () =>
        {
            await Task.Yield();
            tcs.SetResult(true);
        });
        cmd.Execute(null);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task;
        Assert.True(completed, "Async command did not complete within 2 seconds");
    }
}
