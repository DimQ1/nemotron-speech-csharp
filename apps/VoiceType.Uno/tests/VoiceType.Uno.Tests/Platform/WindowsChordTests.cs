using VoiceType.Uno.Services.Platform;
using Xunit;

namespace VoiceType.Uno.Tests.Platform;

/// <summary>The paste shortcut of the Windows injector: settings string to virtual keys (SendInput itself needs a desktop).</summary>
public class WindowsChordTests
{
    private const ushort Ctrl = 0x11, Shift = 0x10, Alt = 0x12, Win = 0x5B, Insert = 0x2D, V = 0x56;

    [Theory]
    [InlineData("Ctrl+V", new ushort[] { Ctrl }, V)]
    [InlineData("Ctrl+Shift+V", new ushort[] { Ctrl, Shift }, V)]
    [InlineData("Shift+Insert", new ushort[] { Shift }, Insert)]
    [InlineData("ctrl + alt + v", new ushort[] { Ctrl, Alt }, V)]
    [InlineData("Win+7", new ushort[] { Win }, (ushort)'7')]
    [InlineData("Control+Enter", new ushort[] { Ctrl }, (ushort)0x0D)]
    public void ParseChord_ShouldMapThePresetsAndHandEditedChords(string chord, ushort[] modifiers, ushort key)
    {
        var parsed = WindowsTextInjector.ParseChord(chord);

        Assert.Equal(modifiers, parsed.Modifiers);
        Assert.Equal(key, parsed.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Banana")]
    public void ParseChord_ShouldFallBackToCtrlV_WhenTheChordMakesNoSense(string? chord)
    {
        var parsed = WindowsTextInjector.ParseChord(chord);

        Assert.Equal(new ushort[] { Ctrl }, parsed.Modifiers);
        Assert.Equal(V, parsed.Key);
    }
}
