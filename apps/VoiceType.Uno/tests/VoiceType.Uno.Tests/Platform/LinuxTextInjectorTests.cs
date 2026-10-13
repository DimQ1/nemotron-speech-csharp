using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Platform.Linux;
using Xunit;

namespace VoiceType.Uno.Tests.Platform;

public class LinuxTextInjectorTests
{
    private sealed class FakeKeyboard(bool available = true, bool canType = true, int typeDelayMs = 0) : ILinuxKeyboard
    {
        private int _active;
        public int MaxConcurrent;
        public readonly List<string> Typed = [];
        public readonly List<string> Chords = [];

        public bool IsAvailable => available;
        public bool CanType(string text) => canType;

        public void TypeText(string text)
        {
            var now = Interlocked.Increment(ref _active);
            lock (Typed)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, now);
                Typed.Add(text);
            }
            if (typeDelayMs > 0)
                Thread.Sleep(typeDelayMs);
            Interlocked.Decrement(ref _active);
        }

        public void PressChord(string chord) => Chords.Add(chord);
    }

    private sealed class FakeClipboard(bool available = true) : ILinuxClipboard
    {
        public readonly List<string> Set = [];
        public bool IsAvailable => available;
        public void SetText(string text) => Set.Add(text);
    }

    [Fact]
    public void Inject_ShouldTypeDirectlyAndLeaveClipboardAlone_WhenKeyboardCanType()
    {
        var keyboard = new FakeKeyboard();
        var clipboard = new FakeClipboard();

        new LinuxTextInjector(clipboard, keyboard).Inject("Hello, мир!");

        Assert.Equal(["Hello, мир!"], keyboard.Typed);
        Assert.Empty(clipboard.Set);
        Assert.Empty(keyboard.Chords);
    }

    [Fact]
    public void Inject_ShouldPasteThroughClipboard_WhenKeyboardCannotTypeTheText()
    {
        var keyboard = new FakeKeyboard(canType: false);
        var clipboard = new FakeClipboard();

        new LinuxTextInjector(clipboard, keyboard).Inject("привет");

        Assert.Empty(keyboard.Typed);
        Assert.Equal(["привет"], clipboard.Set);
        Assert.Equal(["Ctrl+V"], keyboard.Chords);
    }

    [Fact]
    public void Inject_ShouldPasteWithTheConfiguredChord_WhenModeIsPaste_EvenIfTheKeyboardCouldType()
    {
        var keyboard = new FakeKeyboard();
        var clipboard = new FakeClipboard();
        var injector = new LinuxTextInjector(clipboard, keyboard) { Mode = TextInjectionMode.Paste, PasteChord = "Ctrl+Shift+V" };

        injector.Inject("hello");

        Assert.Empty(keyboard.Typed);
        Assert.Equal(["hello"], clipboard.Set);
        Assert.Equal(["Ctrl+Shift+V"], keyboard.Chords);
    }

    [Fact]
    public void Inject_ShouldFallBackToTyping_WhenModeIsPasteButThereIsNoClipboardTool()
    {
        var keyboard = new FakeKeyboard();

        new LinuxTextInjector(new FakeClipboard(available: false), keyboard) { Mode = TextInjectionMode.Paste }.Inject("hello");

        Assert.Equal(["hello"], keyboard.Typed);
    }

    [Fact]
    public void KeyMap_ShouldKnowInsert_SoShiftInsertIsNotShiftSpace()
    {
        Assert.Equal("42:1 110:1 110:0 42:0", LinuxKeyMap.ToYdotoolChord("Shift+Insert"));
        Assert.Equal("shift+Insert", LinuxKeyMap.ToXdotoolChord("Shift+Insert"));
        Assert.Equal((nuint)0xFF63, LinuxKeyMap.KeyKeysym("Insert"));
    }

    [Fact]
    public void Inject_ShouldTypeDirectly_WhenThereIsNoClipboardTool()
    {
        var keyboard = new FakeKeyboard();

        new LinuxTextInjector(new FakeClipboard(available: false), keyboard).Inject("text");

        Assert.Equal(["text"], keyboard.Typed);
    }

    [Fact]
    public void Inject_ShouldOnlySetClipboard_WhenThereIsNoKeyboardBackend()
    {
        var keyboard = new FakeKeyboard(available: false);
        var clipboard = new FakeClipboard();

        new LinuxTextInjector(clipboard, keyboard).Inject("text");

        Assert.Empty(keyboard.Typed);
        Assert.Empty(keyboard.Chords);
        Assert.Equal(["text"], clipboard.Set);
    }

    [Fact]
    public void Inject_ShouldDoNothing_ForEmptyText()
    {
        var keyboard = new FakeKeyboard();
        var clipboard = new FakeClipboard();
        var injector = new LinuxTextInjector(clipboard, keyboard);

        injector.Inject("");
        injector.Inject(null!);

        Assert.Empty(keyboard.Typed);
        Assert.Empty(clipboard.Set);
    }

    [Fact]
    public void CopyToClipboard_ShouldNeverTypeOrPaste()
    {
        var keyboard = new FakeKeyboard();
        var clipboard = new FakeClipboard();

        new LinuxTextInjector(clipboard, keyboard).CopyToClipboard("copy me");

        Assert.Equal(["copy me"], clipboard.Set);
        Assert.Empty(keyboard.Typed);
        Assert.Empty(keyboard.Chords);
    }

    [Fact]
    public async Task Inject_ShouldNotInterleaveConcurrentCalls()
    {
        var keyboard = new FakeKeyboard(typeDelayMs: 25);
        var injector = new LinuxTextInjector(new FakeClipboard(), keyboard);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => injector.Inject($"sentence {i}"))));

        Assert.Equal(6, keyboard.Typed.Count);
        Assert.Equal(1, keyboard.MaxConcurrent);
    }

    [Fact]
    public void YdotoolCanType_ShouldAcceptAsciiAndRejectEverythingElse()
    {
        var ydotool = new YdotoolKeyboard();

        Assert.True(ydotool.CanType("Hello, World! ~{}\n\t"));
        Assert.False(ydotool.CanType("привет"));
        Assert.False(ydotool.CanType("café"));
        Assert.False(ydotool.CanType("emoji 😀"));
    }
}
