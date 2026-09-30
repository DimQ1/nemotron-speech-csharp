using SpeechLib.ModelDownload;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class DownloadProgressFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536 * 1024, "1.5 MB")]
    [InlineData(3L << 30, "3.00 GB")]
    public void FormatBytes_ShouldUseTheExpectedUnit(long bytes, string expected) =>
        Assert.Equal(expected, DownloadProgressFormatter.FormatBytes(bytes));

    [Fact]
    public void DescribeQueue_ShouldBeInactive_WhenNothingIsListed()
    {
        var progress = DownloadProgressFormatter.DescribeQueue(new DownloadTotals(0, 0, 0, 0, 0, 0));

        Assert.False(progress.IsActive);
        Assert.Equal("", progress.Text);
        Assert.Equal(DownloadQueueProgress.Empty, progress);
    }

    [Fact]
    public void DescribeQueue_ShouldReportPercentBytesAndJobCounts()
    {
        var totals = new DownloadTotals(
            Active: 1,
            Completed: 1,
            Failed: 0,
            DownloadedBytes: 512L * 1024 * 1024,
            TotalBytes: 1024L * 1024 * 1024,
            BytesPerSecond: 0);

        var progress = DownloadProgressFormatter.DescribeQueue(totals);

        Assert.True(progress.IsActive);
        Assert.Equal(50, progress.Percent);
        Assert.Equal("Downloading models: 50% (512.0 MB / 1.00 GB, 1/2 done)", progress.Text);
    }

    [Fact]
    public void DescribeQueue_ShouldReportIndeterminateProgress_WhenSizesAreUnknown()
    {
        var progress = DownloadProgressFormatter.DescribeQueue(new DownloadTotals(2, 0, 0, 0, 0, 0));

        Assert.True(progress.IsActive);
        Assert.Equal("Downloading models... (0/2 done)", progress.Text);
    }

    [Fact]
    public void DescribeQueue_ShouldCountFailuresInTheJobTotal()
    {
        var progress = DownloadProgressFormatter.DescribeQueue(new DownloadTotals(1, 1, 2, 0, 0, 0));

        Assert.Equal("Downloading models... (1/4 done)", progress.Text);
    }

    [Fact]
    public void DescribeQueue_ShouldStayInactive_WhenOnlyFinishedJobsRemain()
    {
        var totals = new DownloadTotals(
            Active: 0,
            Completed: 2,
            Failed: 0,
            DownloadedBytes: 1024,
            TotalBytes: 1024,
            BytesPerSecond: 0);

        var progress = DownloadProgressFormatter.DescribeQueue(totals);

        Assert.False(progress.IsActive);
        Assert.Equal("Downloading models: 100% (1 KB / 1 KB, 2/2 done)", progress.Text);
    }
}
