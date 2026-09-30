using VoiceType.Uno.Services;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests;

/// <summary>
/// Covers the rules that used to live inline in MainViewModel: the partial-result
/// throttle, the manual-input hand-off, and appending speech after existing text.
/// </summary>
public class TranscriptCoordinatorTests
{
    [Fact]
    public void PartialResults_ShouldOpenExactlyOneThrottleWindow()
    {
        var (coordinator, timer, published) = CreateCoordinator();
        coordinator.BeginRecordingSession(clearTextOnSessionStart: false);

        coordinator.OnPartialResult("hello");
        coordinator.OnPartialResult("hello world");
        coordinator.OnPartialResult("hello world again");

        Assert.Equal(1, timer.StartCount);
        Assert.Empty(published);
    }

    [Fact]
    public void ThrottleWindow_ShouldPublishTheNewestPartial()
    {
        var (coordinator, timer, published) = CreateCoordinator();
        coordinator.BeginRecordingSession(clearTextOnSessionStart: false);
        coordinator.OnPartialResult("hello");
        coordinator.OnPartialResult("hello world");

        timer.Tick();

        Assert.Equal(["hello world"], published);
    }

    [Fact]
    public void ThrottleWindow_ShouldOpenAgainAfterItFires()
    {
        var (coordinator, timer, published) = CreateCoordinator();
        coordinator.BeginRecordingSession(clearTextOnSessionStart: false);

        coordinator.OnPartialResult("one");
        timer.Tick();
        coordinator.OnPartialResult("one two");
        timer.Tick();

        Assert.Equal(["one", "one two"], published);
    }

    [Fact]
    public void FinalResult_ShouldDropThePendingPartial()
    {
        var (coordinator, timer, published) = CreateCoordinator();
        coordinator.BeginRecordingSession(clearTextOnSessionStart: false);
        coordinator.OnPartialResult("hello wor");

        coordinator.OnFinalResult("hello world");
        var commitsBeforeStaleTick = published.Count;
        timer.Tick();

        Assert.False(timer.IsRunning);
        Assert.Equal(["hello world"], published);
        Assert.Equal(commitsBeforeStaleTick, published.Count);
    }

    [Fact]
    public void FinalResult_ShouldRaiseFinalCommitted()
    {
        var (coordinator, _, _) = CreateCoordinator();
        var committed = new List<string>();
        coordinator.FinalCommitted += committed.Add;

        coordinator.OnFinalResult("done");

        Assert.Equal(["done"], committed);
    }

    [Fact]
    public void ManualInput_ShouldIgnoreSpeechResults()
    {
        var (coordinator, timer, published) = CreateCoordinator();
        coordinator.SetManualInput(true);

        coordinator.OnPartialResult("typed over");
        timer.Tick();
        coordinator.OnFinalResult("typed over");

        Assert.Empty(published);
    }

    [Fact]
    public void BeginRecordingSession_ShouldClearTheFieldWhenTheSettingAsksForIt()
    {
        var (coordinator, _, published) = CreateCoordinator("previous transcript");

        var cleared = coordinator.BeginRecordingSession(clearTextOnSessionStart: true);

        Assert.True(cleared);
        Assert.Equal([""], published);
    }

    [Fact]
    public void BeginRecordingSession_ShouldKeepTextTypedInManualInput()
    {
        var (coordinator, _, published) = CreateCoordinator();
        coordinator.SetManualInput(true);
        coordinator.SetManualInput(false);

        var cleared = coordinator.BeginRecordingSession(clearTextOnSessionStart: true);

        Assert.False(cleared);
        Assert.Empty(published);
    }

    [Fact]
    public void SpeechShouldAppendAfterExistingText()
    {
        var (coordinator, _, published) = CreateCoordinator("typed words");
        coordinator.BeginRecordingSession(clearTextOnSessionStart: false);

        coordinator.OnFinalResult("spoken");

        Assert.Equal(["typed words spoken"], published);
    }

    [Fact]
    public void StartNewSpeechSegment_ShouldTreatTheNextSessionTextAsEntirelyNew()
    {
        var (coordinator, _, published) = CreateCoordinator("typed");
        coordinator.BeginRecordingSession(clearTextOnSessionStart: false);
        coordinator.OnFinalResult("one two");

        // Capture was torn down and restarted: the recognizer begins a new session text,
        // so none of it is skipped as already-known speech.
        coordinator.StartNewSpeechSegment();
        coordinator.OnFinalResult("three");

        Assert.Equal(["typed one two", "typed one two three"], published);
    }

    [Fact]
    public void Dispose_ShouldStopTheThrottleWindow()
    {
        var (coordinator, timer, _) = CreateCoordinator();
        coordinator.OnPartialResult("hello");

        coordinator.Dispose();

        Assert.False(timer.IsRunning);
    }

    /// <summary>
    /// Wires a coordinator to a deterministic scheduler and a transcript field that
    /// behaves like the ViewModel's <c>FloatingText</c> (it is updated by the event).
    /// </summary>
    private static (TranscriptCoordinator Coordinator, FakeUiTimer Timer, List<string> Published) CreateCoordinator(
        string initialText = "")
    {
        var scheduler = new FakeUiScheduler();
        var text = initialText;
        var published = new List<string>();

        var coordinator = new TranscriptCoordinator(scheduler, () => text);
        coordinator.TextChanged += value =>
        {
            published.Add(value);
            text = value;
        };

        return (coordinator, scheduler.Timer, published);
    }
}
