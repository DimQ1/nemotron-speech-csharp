using SpeechLib.TextOutput;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_SpeechAppendComposerTests
{
    [Fact]
    public void Compose_EmptyField_ShowsRecognizerText()
    {
        var composer = new SpeechAppendComposer();
        composer.ContinueAfter("", "");

        Assert.Equal("Hello world", composer.Compose("Hello world"));
    }

    [Fact]
    public void Compose_AppendsSpeechAfterTypedText()
    {
        var composer = new SpeechAppendComposer();
        composer.ContinueAfter("Typed by hand.", "");

        Assert.Equal("Typed by hand. And dictated", composer.Compose("And dictated"));
    }

    [Fact]
    public void Compose_AfterManualEdit_SkipsSpeechAlreadyShown()
    {
        var composer = new SpeechAppendComposer();
        // Dictated "Hello world", switched to manual input, edited, switched back.
        composer.ContinueAfter("Hello, dear world!", "Hello world");

        Assert.Equal("Hello, dear world! Next words", composer.Compose("Hello world Next words"));
    }

    [Fact]
    public void Compose_RevisedPreviewTail_DoesNotDuplicate()
    {
        var composer = new SpeechAppendComposer();
        composer.ContinueAfter("Edited text.", "One two thre");

        // The recognizer revises the provisional last word and continues.
        Assert.Equal("Edited text. four", composer.Compose("One two three four"));
    }

    [Fact]
    public void Compose_NewRecognizerSession_AppendsEverything()
    {
        var composer = new SpeechAppendComposer();
        composer.ContinueAfter("Kept text.", "Old session words");

        Assert.Equal("Kept text. Brand new", composer.Compose("Brand new"));
        // Once a new session is detected, later updates keep extending it.
        Assert.Equal("Kept text. Brand new speech", composer.Compose("Brand new speech"));
    }

    [Fact]
    public void Compose_FieldEndingWithWhitespace_AddsNoExtraSpace()
    {
        var composer = new SpeechAppendComposer();
        composer.ContinueAfter("Line one" + Environment.NewLine, "");

        Assert.Equal("Line one" + Environment.NewLine + "Line two", composer.Compose("Line two"));
    }

    [Fact]
    public void Compose_NoNewSpeech_KeepsFieldUnchanged()
    {
        var composer = new SpeechAppendComposer();
        composer.ContinueAfter("Edited.", "Hello world");

        Assert.Equal("Edited.", composer.Compose("Hello world"));
    }

    [Fact]
    public void Compose_CustomSeparator_ShouldJoinWithIt()
    {
        var composer = new SpeechAppendComposer();
        composer.ContinueAfter("Previous session", "", Environment.NewLine);

        Assert.Equal("Previous session", composer.Compose(""));
        Assert.Equal("Previous session" + Environment.NewLine + "New one", composer.Compose("New one"));
    }
}
