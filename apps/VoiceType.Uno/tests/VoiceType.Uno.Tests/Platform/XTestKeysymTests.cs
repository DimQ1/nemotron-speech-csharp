using VoiceType.Uno.Services.Platform.Linux;
using Xunit;

namespace VoiceType.Uno.Tests.Platform;

/// <summary>The pure parts of the X11 typist: text to keysyms, chunking, leftover detection.</summary>
public class XTestKeysymTests
{
    private const nuint Return = 0xFF0D;
    private const nuint Tab = 0xFF09;

    [Fact]
    public void ToKeysyms_ShouldUseLatin1AsIsAndUnicodeRangeForTheRest()
    {
        var keysyms = XTestKeyboard.ToKeysyms("aZ é Ж😀");

        Assert.Equal(
            [(nuint)'a', 'Z', ' ', 0xE9, ' ', 0x01000416, 0x0101F600],
            keysyms);
    }

    [Fact]
    public void ToKeysyms_ShouldMapNewlinesAndTabsAndTreatCrLfAsOneEnter()
    {
        Assert.Equal(
            [(nuint)'a', Return, 'b', Return, 'c', Return, Tab, 'd'],
            XTestKeyboard.ToKeysyms("a\r\nb\rc\n\td"));
    }

    [Fact]
    public void ToKeysyms_ShouldSkipOtherControlCharacters()
    {
        Assert.Equal([(nuint)'a', 'b'], XTestKeyboard.ToKeysyms("a\u0001\u001b\u007f\u0085b"));
    }

    [Fact]
    public void SplitIntoChunks_ShouldStayWithinThePoolAndPreserveOrder()
    {
        var keysyms = XTestKeyboard.ToKeysyms("abcabcdefdefg");

        var chunks = XTestKeyboard.SplitIntoChunks(keysyms, poolSize: 3);

        Assert.All(chunks, c => Assert.True(c.Distinct().Count() <= 3));
        Assert.Equal(keysyms, chunks.SelectMany(c => c).ToList());
        Assert.Equal(3, chunks.Count); // abcabc | defdef | g
    }

    [Fact]
    public void SplitIntoChunks_ShouldReturnSingleChunk_WhenEverythingFits()
    {
        var chunks = XTestKeyboard.SplitIntoChunks(XTestKeyboard.ToKeysyms("hello"), poolSize: 16);

        Assert.Single(chunks);
    }

    [Fact]
    public void SplitIntoChunks_ShouldReturnNothing_ForEmptyInput()
    {
        Assert.Empty(XTestKeyboard.SplitIntoChunks([], poolSize: 16));
    }

    [Fact]
    public void IsLeftoverBinding_ShouldRecognizeOurOwnBinding()
    {
        // What the server reads back after XChangeKeyboardMapping(ks, ks): the group is replicated.
        Assert.True(XTestKeyboard.IsLeftoverBinding([0x48, 0x48, 0x48, 0x48, 0, 0, 0]));
        Assert.True(XTestKeyboard.IsLeftoverBinding([0x01000436, 0x01000436, 0x01000436, 0x01000436, 0, 0, 0]));
        Assert.True(XTestKeyboard.IsLeftoverBinding([0x48, 0x48]));
    }

    [Theory]
    [InlineData(0u, 0u, 0u, 0u, 0u, 0u, 0u)]                       // unused keycode
    [InlineData(0x61u, 0x41u, 0x61u, 0x41u, 0u, 0u, 0u)]           // letter key: a / A
    [InlineData(0x1008ffb5u, 0u, 0x1008ffb5u, 0u, 0u, 0u, 0u)]     // XF86 media key
    [InlineData(0xff1bu, 0u, 0xff1bu, 0u, 0u, 0u, 0u)]             // Escape
    [InlineData(0xff69u, 0xff69u, 0xff69u, 0xff69u, 0u, 0u, 0u)]   // repeated, but not a typable keysym
    [InlineData(0x48u, 0x48u, 0x61u, 0x61u, 0u, 0u, 0u)]           // two different symbols
    public void IsLeftoverBinding_ShouldRejectRealKeysAndUnusedKeycodes(
        uint a, uint b, uint c, uint d, uint e, uint f, uint g)
    {
        Assert.False(XTestKeyboard.IsLeftoverBinding([a, b, c, d, e, f, g]));
    }
}
