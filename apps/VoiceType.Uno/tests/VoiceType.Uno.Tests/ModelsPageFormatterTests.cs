using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class ModelsPageFormatterTests
{
    [Fact]
    public void DescribeInstalled_ShouldSayNothingInstalled_WhenCountIsZero() =>
        Assert.Equal("No models installed", ModelsPageFormatter.DescribeInstalled(0, 0));

    [Fact]
    public void DescribeInstalled_ShouldIncludeDiskUsage_WhenSizeIsKnown() =>
        Assert.Equal("2 installed · 1.50 GB used", ModelsPageFormatter.DescribeInstalled(2, 3L << 29));

    [Fact]
    public void DescribeInstalled_ShouldShowOnlyTheCount_WhenSizeIsUnknown() =>
        Assert.Equal("1 installed", ModelsPageFormatter.DescribeInstalled(1, 0));

    [Theory]
    [InlineData(0, "Downloads")]
    [InlineData(2, "Downloads · 2")]
    public void DownloadsTabLabel_ShouldShowTheActiveCountOnlyWhenPositive(int active, string expected) =>
        Assert.Equal(expected, ModelsPageFormatter.DownloadsTabLabel(active));

    [Theory]
    [InlineData(0, 0, 0, 0, "No downloads")]
    [InlineData(2, 1, 0, 3, "2 active · 1 failed")]
    [InlineData(0, 0, 3, 3, "3 done")]
    [InlineData(0, 0, 0, 2, "2 stopped")]
    public void DescribeQueueHeadline_ShouldListNonEmptyStates(int active, int failed, int completed, int total, string expected) =>
        Assert.Equal(expected, ModelsPageFormatter.DescribeQueueHeadline(active, failed, completed, total));

    [Fact]
    public void DescribeProgress_ShouldCombinePercentAndBytes() =>
        Assert.Equal("50% · 512.0 MB / 1.00 GB", ModelsPageFormatter.DescribeProgress(50, 512L << 20, 1L << 30));

    [Fact]
    public void DescribeProgress_ShouldShowDownloadedBytesOnly_WhenTotalIsUnknown() =>
        Assert.Equal("2.0 MB", ModelsPageFormatter.DescribeProgress(0, 2L << 20, 0));

    [Fact]
    public void DescribeRate_ShouldBeEmpty_WhenNothingIsMoving() =>
        Assert.Equal("", ModelsPageFormatter.DescribeRate(0, 0, 100));

    [Fact]
    public void DescribeRate_ShouldAppendTimeLeft_WhenTotalIsKnown()
    {
        // 420 MB left at 1 MB/s = 7 minutes.
        var text = ModelsPageFormatter.DescribeRate(1 << 20, 100L << 20, 520L << 20);

        Assert.Equal("1.0 MB/s · 7 min", text);
    }

    [Fact]
    public void DescribeRate_ShouldOmitTimeLeft_WhenTotalIsUnknown() =>
        Assert.Equal("1.0 MB/s", ModelsPageFormatter.DescribeRate(1 << 20, 5L << 20, 0));

    [Theory]
    [InlineData(30, "< 1 min")]
    [InlineData(61, "2 min")]
    [InlineData(3900, "1 h 05 min")]
    public void FormatTimeLeft_ShouldRoundToCoarseUnits(int seconds, string expected) =>
        Assert.Equal(expected, ModelsPageFormatter.FormatTimeLeft(TimeSpan.FromSeconds(seconds)));
}
