using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class LiveTextFeedTests
{
    [Fact]
    public void Next_ShouldReturnOnlyTheNewText_AsTheCommittedTranscriptGrows()
    {
        var feed = new LiveTextFeed();
        feed.BeginSession();

        Assert.Equal("Hello world.", feed.Next("Hello world.", canType: true));
        Assert.Equal(" How are you?", feed.Next("Hello world. How are you?", canType: true));
        Assert.Equal("", feed.Next("Hello world. How are you?", canType: true));
        Assert.Equal(" Fine.", feed.Next("Hello world. How are you? Fine.", canType: true));
    }

    [Fact]
    public void Next_ShouldNeverTypeTextCommittedWhileTypingWasNotPossible()
    {
        var feed = new LiveTextFeed();
        feed.BeginSession();
        Assert.Equal("First phrase.", feed.Next("First phrase.", canType: true));

        // Focus is inside VoiceType itself (or injection is off): the phrase is skipped, not queued.
        Assert.Equal("", feed.Next("First phrase. Second phrase.", canType: false));

        // Back in another app: only the words after the skipped ones are typed.
        Assert.Equal(" Third phrase.", feed.Next("First phrase. Second phrase. Third phrase.", canType: true));
    }

    [Fact]
    public void BeginSession_ShouldStartOver_AndJoinTheNextSessionWithASpace()
    {
        var feed = new LiveTextFeed();
        feed.BeginSession();
        Assert.Equal("Hello there.", feed.Next("Hello there.", canType: true));

        feed.BeginSession();

        // A new session's text restarts from nothing; it continues the document after a space.
        Assert.Equal(" Again.", feed.Next("Again.", canType: true));
        Assert.Equal(" And more.", feed.Next("Again. And more.", canType: true));
    }

    [Fact]
    public void Next_ShouldNotRepeatWords_WhenTheFinalPassNormalizesSpacing()
    {
        var feed = new LiveTextFeed();
        feed.BeginSession();
        Assert.Equal("Hello  world", feed.Next("Hello  world", canType: true));

        // The final post-processing collapsed the double space: nothing is typed again ...
        Assert.Equal("", feed.Next("Hello world", canType: true));
        // ... and the words flushed at the end still arrive.
        Assert.Equal(" again", feed.Next("Hello world again", canType: true));
    }

    [Fact]
    public void Next_ShouldReturnNothing_ForEmptyText()
    {
        var feed = new LiveTextFeed();
        feed.BeginSession();

        Assert.Equal("", feed.Next("", canType: true));
        Assert.Equal("", feed.Next("", canType: false));
    }
}
