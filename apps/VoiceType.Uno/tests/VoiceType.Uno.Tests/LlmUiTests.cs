using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class LlmUiTests
{
    private static LlmModelInfo Model(string id) => LlmModelCatalog.Find(id)!;

    private static LlmUiCard Build(string id, LlmModelState state, double? percent = null, double? freeGb = 32,
        string? loading = null, bool isPhone = false) =>
        LlmUiCardBuilder.Build(Model(id), state, percent, freeGb, loading, isPhone: isPhone);

    [Fact]
    public void ModelsFor_ShouldListAllOnDesktopInCatalogOrder()
    {
        var ids = LlmUiCardBuilder.ModelsFor(isPhone: false).Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "gemma-4-e2b", "gemma-4-e4b", "gemma-4-12b" }, ids);
    }

    [Fact]
    public void ModelsFor_ShouldListOnlyE2BOnPhones() =>
        Assert.Equal(new[] { "gemma-4-e2b" }, LlmUiCardBuilder.ModelsFor(isPhone: true).Select(m => m.Id).ToArray());

    [Fact]
    public void Build_ShouldMarkE2BRecommendedAndLargerModelsDesktopOnly()
    {
        var e2b = Build("gemma-4-e2b", LlmModelState.NotInstalled);
        var big = Build("gemma-4-12b", LlmModelState.NotInstalled);

        Assert.Contains(e2b.Chips, c => c.Kind == LlmUiChipKind.Recommended);
        Assert.DoesNotContain(e2b.Chips, c => c.Text == "Desktop only");
        Assert.DoesNotContain(big.Chips, c => c.Kind == LlmUiChipKind.Recommended);
        Assert.Contains(big.Chips, c => c.Text == "Desktop only");
    }

    [Fact]
    public void Build_ShouldShowSizeAndContextChips()
    {
        var card = Build("gemma-4-e4b", LlmModelState.NotInstalled);
        Assert.Contains(card.Chips, c => c.Text == "8K context");
        Assert.Contains(card.Chips, c => c.Text == DownloadProgressFormatter.FormatBytes(Model("gemma-4-e4b").SizeBytes));
    }

    [Theory]
    [InlineData(LlmModelState.NotInstalled, LlmUiAction.Download, "Download", true)]
    [InlineData(LlmModelState.Downloading, LlmUiAction.Cancel, "Cancel", true)]
    [InlineData(LlmModelState.Installed, LlmUiAction.Use, "Use", true)]
    [InlineData(LlmModelState.Active, LlmUiAction.InUse, "In use", false)]
    public void Build_ShouldOfferOnePrimaryActionPerState(LlmModelState state, LlmUiAction action, string text, bool visible)
    {
        var card = Build("gemma-4-e4b", state, percent: 40);
        Assert.Equal(action, card.Action);
        Assert.Equal(text, card.ActionText);
        Assert.Equal(visible, card.ActionVisible);
    }

    [Fact]
    public void Build_ShouldShowInUseChipOnlyForTheActiveModel()
    {
        Assert.Contains(Build("gemma-4-e2b", LlmModelState.Active).Chips, c => c.Kind == LlmUiChipKind.InUse);
        Assert.DoesNotContain(Build("gemma-4-e2b", LlmModelState.Installed).Chips, c => c.Kind == LlmUiChipKind.InUse);
    }

    [Fact]
    public void Build_ShouldWarnWhenFreeRamIsBelowTheNeed()
    {
        var card = Build("gemma-4-12b", LlmModelState.Installed, freeGb: 8);
        Assert.True(card.RamLow);
        Assert.Contains(card.Chips, c => c.Kind == LlmUiChipKind.Warning);
        Assert.Contains("14 GB", card.RamText);
    }

    [Fact]
    public void Build_ShouldNotWarnWhenRamIsUnknownOrEnough()
    {
        Assert.False(Build("gemma-4-12b", LlmModelState.Installed, freeGb: null).RamLow);
        Assert.False(Build("gemma-4-12b", LlmModelState.Installed, freeGb: 14).RamLow);
    }

    [Fact]
    public void Build_ShouldNotWarnForTheActiveModel_BecauseItsOwnMemoryIsAlreadyTaken() =>
        Assert.False(Build("gemma-4-12b", LlmModelState.Active, freeGb: 1).RamLow);

    [Fact]
    public void Build_ShouldReportProgressWhileDownloading()
    {
        var card = Build("gemma-4-e4b", LlmModelState.Downloading, percent: 42.4);
        Assert.True(card.ShowProgress);
        Assert.Equal("Downloading…", card.StatusText);
        Assert.Equal(42.4, card.Percent);
        Assert.False(card.CanDelete);
    }

    [Fact]
    public void Build_ShouldLockSwitchingAndDeleteWhileAnotherModelLoads()
    {
        var other = Build("gemma-4-e4b", LlmModelState.Installed, loading: "gemma-4-12b");
        Assert.False(other.ActionEnabled);
        Assert.False(other.CanDelete);

        var loadingOne = Build("gemma-4-12b", LlmModelState.Installed, loading: "gemma-4-12b");
        Assert.Equal("Loading Gemma 4 12B…", loadingOne.StatusText);
        Assert.Equal("Loading…", loadingOne.ActionText);
    }

    [Fact]
    public void Build_ShouldAllowDeleteOnlyForInstalledInactiveModels()
    {
        Assert.True(Build("gemma-4-e4b", LlmModelState.Installed).CanDelete);
        Assert.False(Build("gemma-4-e2b", LlmModelState.Active).CanDelete);
        Assert.False(Build("gemma-4-e4b", LlmModelState.NotInstalled).CanDelete);
    }

    [Theory]
    [InlineData(4, 3.9, LlmUiRamFit.Low)]
    [InlineData(4, 4.0, LlmUiRamFit.Fits)]
    [InlineData(4, null, LlmUiRamFit.Unknown)]
    public void Evaluate_ShouldCompareNeedWithFreeRam(double need, double? free, LlmUiRamFit expected) =>
        Assert.Equal(expected, LlmUiRamCheck.Evaluate(need, free));

    [Fact]
    public void Describe_ShouldAppendFreeRamWhenKnown()
    {
        Assert.Equal("Needs ≈6 GB free RAM", LlmUiRamCheck.Describe(6, null));
        Assert.Equal("Needs ≈6 GB free RAM · 9.5 GB free now", LlmUiRamCheck.Describe(6, 9.5));
    }

    private sealed class FixedMemory(long? bytes) : IDeviceMemoryProvider
    {
        public long? GetAvailableBytes() => bytes;
    }

    [Fact]
    public void AvailableGb_ShouldUseTheInjectedProvider()
    {
        var saved = DeviceMemory.Provider;
        try
        {
            DeviceMemory.Provider = new FixedMemory(8L << 30);
            Assert.Equal(8d, DeviceMemory.AvailableGb);
            DeviceMemory.Provider = new FixedMemory(null);
            Assert.Null(DeviceMemory.AvailableGb);
        }
        finally
        {
            DeviceMemory.Provider = saved;
        }
    }

    [Fact]
    public void GcProvider_ShouldReportSomeMemoryOnThisMachine() =>
        Assert.True(new GcDeviceMemoryProvider().GetAvailableBytes() is null or >= 0);

    [Fact]
    public void Rate_ShouldBeZeroUntilTwoSamplesAndThenFollowThePercent()
    {
        var rate = new LlmUiDownloadRate();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var total = 1_000_000_000L;

        Assert.Equal(0, rate.Update(0, total, t0));
        // 1% of 1 GB in 2 s = 5 MB/s
        Assert.Equal(5_000_000, rate.Update(1, total, t0.AddSeconds(2)), 1);
    }

    [Fact]
    public void Rate_ShouldRestartWhenThePercentGoesBack()
    {
        var rate = new LlmUiDownloadRate();
        var t0 = DateTime.UnixEpoch;
        rate.Update(10, 1_000_000, t0);
        rate.Update(20, 1_000_000, t0.AddSeconds(1));
        Assert.Equal(0, rate.Update(0, 1_000_000, t0.AddSeconds(2)));
    }

    [Fact]
    public void RateText_ShouldReuseTheSharedFormatter()
    {
        var text = ModelsPageFormatter.DescribeRate(5L << 20, 0, 600L << 20);
        Assert.StartsWith("5.0 MB/s", text);
    }

    [Fact]
    public void SwitchError_ShouldExplainMemoryFailuresAndPassOthersThrough()
    {
        var big = Model("gemma-4-12b");
        Assert.Contains("Not enough memory", LlmUiText.SwitchError(big, new OutOfMemoryException()));
        Assert.Contains("boom", LlmUiText.SwitchError(big, new InvalidOperationException("boom")));
    }

    [Fact]
    public void Texts_ShouldNameTheActiveModel()
    {
        var m = Model("gemma-4-e4b");
        Assert.Equal("E4B", LlmUiText.ShortName(m));
        Assert.Equal("Gemma 4 E4B, on-device", LlmUiText.SideMenuSubtitle(m.Title));
        Assert.StartsWith("Gemma 4 E4B is not downloaded", LlmUiText.BannerMissing(m.Title));
        Assert.Contains("Gemma 4 E4B", LlmUiText.ChatMissingDetail(m));
    }
}
