using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

/// <summary>The sizes are the ones the design's layout check covers (§6).</summary>
public class MainLayoutTests
{
    [Theory]
    [InlineData(360, 800, true, MainLayoutState.Narrow, CardArrangement.Stacked)]
    [InlineData(320, 568, true, MainLayoutState.Narrow, CardArrangement.Stacked)]
    [InlineData(360, 800, false, MainLayoutState.Narrow, CardArrangement.TranscriptOnly)]
    [InlineData(1280, 800, true, MainLayoutState.Wide, CardArrangement.SideBySide)]
    [InlineData(800, 360, true, MainLayoutState.Short, CardArrangement.SideBySide)]
    [InlineData(320, 420, true, MainLayoutState.Compact, CardArrangement.Tabbed)]
    [InlineData(500, 520, true, MainLayoutState.Compact, CardArrangement.Tabbed)]
    [InlineData(320, 420, false, MainLayoutState.Short, CardArrangement.TranscriptOnly)]
    [InlineData(500, 500, false, MainLayoutState.Narrow, CardArrangement.TranscriptOnly)]
    public void Compute_ShouldPickTheStateForTheWindowSize(
        double width, double height, bool translation, MainLayoutState state, CardArrangement cards)
    {
        var layout = MainLayout.Compute(width, height, translation, isKeyboardOpen: false);

        Assert.Equal(state, layout.State);
        Assert.Equal(cards, layout.Cards);
    }

    [Theory]
    [InlineData(true, CardArrangement.TranslationCollapsed)]
    [InlineData(false, CardArrangement.TranscriptOnly)]
    public void Compute_ShouldCollapseTheTranslationWhileTheKeyboardIsOpen(bool translation, CardArrangement cards)
    {
        var layout = MainLayout.Compute(320, 300, translation, isKeyboardOpen: true);

        Assert.Equal(MainLayoutState.Keyboard, layout.State);
        Assert.Equal(cards, layout.Cards);
        Assert.True(layout.IsCompactChrome);
    }

    [Theory]
    [InlineData(800, 479, true)]
    [InlineData(800, 480, false)]
    [InlineData(360, 800, false)]
    public void Compute_ShouldUseCompactChromeOnShortWindows(double width, double height, bool expected)
    {
        Assert.Equal(expected, MainLayout.Compute(width, height, true, false).IsCompactChrome);
    }

    [Theory]
    [InlineData(360, 520, true)]
    [InlineData(360, 521, false)]
    public void Compute_ShouldUseOneLineBannersUpTo520PixelsTall(double width, double height, bool expected)
    {
        Assert.Equal(expected, MainLayout.Compute(width, height, false, false).IsCompactBanner);
    }

    [Theory]
    [InlineData(839, 16)]
    [InlineData(840, 24)]
    public void Compute_ShouldWidenTheSidePaddingOnWideWindows(double width, double expected)
    {
        Assert.Equal(expected, MainLayout.Compute(width, 800, true, false).SidePadding);
    }

    [Theory]
    [InlineData(360, 744, true, false, SourceChipPlacement.Row)]     // 360x800 phone after the system bars
    [InlineData(1280, 800, true, false, SourceChipPlacement.Row)]
    [InlineData(480, 560, true, false, SourceChipPlacement.Row)]     // the smallest window that still has the row
    [InlineData(480, 559, true, false, SourceChipPlacement.TopBar)]
    [InlineData(320, 496, true, false, SourceChipPlacement.TopBar)]  // 320x568 phone after the system bars
    [InlineData(800, 336, true, false, SourceChipPlacement.TopBar)]  // phone landscape
    [InlineData(320, 420, true, false, SourceChipPlacement.TopBar)]  // split screen
    [InlineData(360, 800, false, true, SourceChipPlacement.TopBar)]  // on-screen keyboard: no spare row whatever the height
    public void Compute_ShouldPlaceTheSourceChipInARowOrTheTopBar(
        double width, double height, bool translation, bool keyboard, SourceChipPlacement expected)
    {
        Assert.Equal(expected, MainLayout.Compute(width, height, translation, keyboard).SourceChip);
    }

    [Theory]
    [InlineData(360, 800, false)]
    [InlineData(320, 300, true)]
    public void Compute_ShouldNotShowTheSourceChipWhereThereIsNoChoice(double width, double height, bool keyboard)
    {
        Assert.Equal(SourceChipPlacement.None, MainLayout.Compute(width, height, false, keyboard, hasSourceChoice: false).SourceChip);
    }
}
