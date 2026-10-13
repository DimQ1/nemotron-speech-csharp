using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests.Settings;

public class HotkeyTextTests
{
    [Fact]
    public void Chips_ShouldSplitTheChordIntoKeys()
    {
        Assert.Equal(["Ctrl", "Shift", "V"], HotkeyText.Chips("Ctrl+Shift+V"));
        Assert.Equal(["Win", "F5"], HotkeyText.Chips("Super+F5"));
        Assert.Empty(HotkeyText.Chips(""));
        Assert.Empty(HotkeyText.Chips(null));
    }

    [Fact]
    public void Compose_ShouldOrderModifiersAndEndWithTheKey()
    {
        Assert.Equal("Ctrl+Shift+V", HotkeyText.Compose(true, true, false, false, "V"));
        Assert.Equal("Ctrl+Alt+Super+F5", HotkeyText.Compose(true, false, true, true, "F5"));
        Assert.Equal("Space", HotkeyText.Compose(false, false, false, false, "Space"));
    }

    [Theory]
    [InlineData("Ctrl+Shift+V", null)]
    [InlineData("Alt+F4", null)]
    [InlineData("Super+Space", null)]
    [InlineData("", null)]
    [InlineData("V", HotkeyText.NeedsModifier)]
    [InlineData("Shift+V", HotkeyText.NeedsModifier)]
    [InlineData("Shift+Insert", HotkeyText.NeedsModifier)]
    public void Validate_ShouldRequireCtrlAltOrWin(string chord, string? error)
        => Assert.Equal(error, HotkeyText.Validate(chord));

    [Fact]
    public void DuplicateOf_ShouldFindTheOtherActionUsingTheChord()
    {
        var settings = new AppSettings { ToggleHotkey = "Ctrl+Shift+V", MuteHotkey = "Ctrl+Shift+M", InjectTextHotkey = "Ctrl+Shift+I" };

        Assert.Equal(HotkeyAction.ToggleRecording, HotkeyText.DuplicateOf(settings, HotkeyAction.ToggleMute, "ctrl+shift+v"));
        Assert.Null(HotkeyText.DuplicateOf(settings, HotkeyAction.ToggleMute, "Ctrl+Shift+M"));
        Assert.Null(HotkeyText.DuplicateOf(settings, HotkeyAction.ToggleMute, ""));
        Assert.Equal("Ctrl+Shift+V is already used for “Start or stop recording”.", HotkeyText.DuplicateMessage("Ctrl+Shift+V", HotkeyAction.ToggleRecording));
    }

    [Fact]
    public void ConflictBanner_ShouldNameTheChordAndWhatHasNoShortcut()
    {
        Assert.Equal("", HotkeyText.ConflictBanner([]));
        Assert.Equal(
            "Ctrl+Shift+M is taken by another app. Mute has no shortcut until you choose a different one.",
            HotkeyText.ConflictBanner([(HotkeyAction.ToggleMute, "Ctrl+Shift+M")]));
        Assert.Equal(
            "Ctrl+Shift+V, Ctrl+Shift+I are taken by other apps. Start/stop, Type transcript have no shortcut until you choose different ones.",
            HotkeyText.ConflictBanner([(HotkeyAction.ToggleRecording, "Ctrl+Shift+V"), (HotkeyAction.InjectText, "Ctrl+Shift+I")]));
    }

    [Fact]
    public void SetChord_AndChord_ShouldRoundTripEveryAction()
    {
        var settings = new AppSettings();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            HotkeyText.SetChord(settings, action, "Ctrl+" + action);
            Assert.Equal("Ctrl+" + action, HotkeyText.Chord(settings, action));
        }
    }
}
