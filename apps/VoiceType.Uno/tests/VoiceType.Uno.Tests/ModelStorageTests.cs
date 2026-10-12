using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class ModelStorageTests
{
    [Fact]
    public void SizeOf_ShouldBeZero_WhenPathDoesNotExist()
    {
        using var temp = new TempDirectory();

        Assert.Equal(0, ModelStorage.SizeOf(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void SizeOf_ShouldSumEveryFileUnderAFolder()
    {
        using var temp = new TempDirectory();
        temp.Write("model/encoder.onnx", "12345");
        temp.Write("model/sub/decoder.onnx", "123");

        Assert.Equal(8, ModelStorage.SizeOf(Path.Combine(temp.Path, "model")));
    }

    [Fact]
    public void SizeOf_ShouldReturnTheFileLength_ForASingleFile()
    {
        using var temp = new TempDirectory();
        temp.Write("gemma.litertlm", "1234");

        Assert.Equal(4, ModelStorage.SizeOf(Path.Combine(temp.Path, "gemma.litertlm")));
    }
}
