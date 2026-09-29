using SpeechLib.ModelDownload;
using Xunit;

namespace SpeechLib.ModelDownload.Tests;

/// <summary>
/// Regression guard for the multi-precision repositories (Parakeet fp32/int8/int4):
/// selecting a subfolder must strip the prefix from the LOCAL path only. Building
/// the download URL from the stripped path produced /resolve/main/config.json and
/// a 404 for every variant stored in a subfolder.
/// </summary>
public sealed class Unit_ModelFileSelectionTests
{
    private static RemoteFile File(string repoPath, long size = 10)
        => new(repoPath, repoPath, size);

    [Fact]
    public void ForSubfolder_KeepsTheRepoPathAndStripsThePrefixLocally()
    {
        var files = new[] { File("int4/config.json"), File("int4/encoder-model.onnx"), File("fp32/config.json") };

        var selected = ModelFileSelection.ForSubfolder(files, "int4");

        Assert.Equal(2, selected.Count);
        Assert.All(selected, file => Assert.StartsWith("int4/", file.RepoPath, StringComparison.Ordinal));
        Assert.Equal("config.json", selected[0].LocalPath);
        Assert.Equal("int4/config.json", selected[0].RepoPath);
    }

    [Fact]
    public void ForSubfolder_ReturnsNothingWhenTheSubfolderIsMissing()
    {
        var files = new[] { File("fp32/config.json"), File("int8/config.json") };

        Assert.Empty(ModelFileSelection.ForSubfolder(files, "int4"));
    }

    [Fact]
    public void ForSubfolder_KeepsRootFilesWhenNoSubfolderIsRequested()
    {
        var files = new[] { File("config.json"), File("encoder-model.onnx"), File("int4/config.json") };

        var selected = ModelFileSelection.ForSubfolder(files, null);

        Assert.Equal(3, selected.Count);
        Assert.Equal("config.json", selected[0].LocalPath);
        Assert.Equal("int4/config.json", selected[2].LocalPath);
    }

    [Theory]
    [InlineData("int4")]
    [InlineData("int4/")]
    public void ForSubfolder_ToleratesATrailingSlash(string subfolder)
    {
        var selected = ModelFileSelection.ForSubfolder(new[] { File("int4/vocab.txt") }, subfolder);

        Assert.Single(selected);
        Assert.Equal("vocab.txt", selected[0].LocalPath);
    }

    [Fact]
    public void ForSubfolder_DoesNotTouchTheSizes()
    {
        var selected = ModelFileSelection.ForSubfolder(new[] { File("int4/encoder-model.onnx", 679_397_024) }, "int4");

        Assert.Equal(679_397_024, Assert.Single(selected).SizeBytes);
    }

    [Fact]
    public void ForSubfolder_SkipsEntriesWithoutARepoPath()
    {
        var files = new[] { new RemoteFile("", "", 0), File("config.json") };

        Assert.Single(ModelFileSelection.ForSubfolder(files, null));
    }
}
