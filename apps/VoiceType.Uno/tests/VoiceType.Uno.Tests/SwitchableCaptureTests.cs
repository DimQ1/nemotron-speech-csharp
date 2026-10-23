using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class SwitchableCaptureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeAudioSourceFactory _factory = new();
    private readonly ConcurrentQueueWrapper _buffer = new(capacity: 100_000);
    private readonly ManualResetEventSlim _signal = new(false);

    private SwitchableCapture Create() => new(_factory, 16000, _buffer, _signal) { StartGrace = TimeSpan.FromMilliseconds(120) };

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static float Marker(CaptureMode mode) => -1000 - (float)mode;

    /// <summary>Everything queued so far, in order: an audio batch as its source tag, a switch marker as <see cref="Marker"/>.</summary>
    private List<float> Drain(SwitchableCapture capture)
    {
        var items = new List<float>();
        while (_buffer.TryDequeue(out var batch))
            items.Add(capture.IsSwitchMarker(batch, out var mode) ? Marker(mode) : batch[0]);
        return items;
    }

    [Fact]
    public async Task Start_ShouldFillTheQueueFromTheRequestedSource()
    {
        using var capture = Create();

        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 3, "three batches");

        Assert.Equal(CaptureMode.Mic, capture.Mode);
        Assert.True(capture.IsActive);
        Assert.All(Drain(capture), tag => Assert.Equal(1f, tag));
        capture.Stop();
    }

    [Fact]
    public async Task SwitchTo_ShouldReplaceTheSource_AndKeepTheSameQueue()
    {
        using var capture = Create();
        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 3, "audio from the microphone");

        var result = await capture.SwitchToAsync(CaptureMode.Loopback);
        await WaitUntil(() => _buffer.Count >= 6, "audio from system audio");

        Assert.Equal(CaptureSwitchOutcome.Switched, result.Outcome);
        Assert.Equal(CaptureMode.Loopback, result.Active);
        Assert.Equal(CaptureMode.Loopback, capture.Mode);

        var items = Drain(capture);
        var marker = items.IndexOf(Marker(CaptureMode.Loopback));
        Assert.True(marker > 0, "the marker of the switch is in the stream");
        Assert.All(items.Take(marker), tag => Assert.Equal(1f, tag));       // only the microphone before it
        Assert.All(items.Skip(marker + 1), tag => Assert.Equal(2f, tag));   // only system audio after it

        var microphone = _factory.Sources[0];
        Assert.True(microphone.Finished);
        Assert.True(microphone.Disposed);
        Assert.False(_factory.Sources[1].Finished);
        capture.Stop();
    }

    [Fact]
    public async Task SwitchTo_TheSameSource_ShouldDoNothing()
    {
        using var capture = Create();
        capture.Start(CaptureMode.Mic);

        var result = await capture.SwitchToAsync(CaptureMode.Mic);

        Assert.Equal(CaptureSwitchOutcome.Unchanged, result.Outcome);
        Assert.Single(_factory.Sources);
        Assert.False(_factory.Sources[0].Disposed);
        capture.Stop();
    }

    [Fact]
    public async Task SwitchTo_BeforeStartAndAfterStop_ShouldReportNotRunning()
    {
        using var capture = Create();
        Assert.Equal(CaptureSwitchOutcome.NotRunning, (await capture.SwitchToAsync(CaptureMode.Loopback)).Outcome);

        capture.Start(CaptureMode.Mic);
        capture.Stop();
        await WaitUntil(() => !capture.IsActive, "capture to end");

        Assert.Equal(CaptureSwitchOutcome.NotRunning, (await capture.SwitchToAsync(CaptureMode.Loopback)).Outcome);
        Assert.Single(_factory.Sources);
    }

    [Fact]
    public async Task SwitchTo_ASourceThatCannotBeCreated_ShouldRefuseAndKeepCapturing()
    {
        _factory.Unavailable.Add(CaptureMode.Mix);
        using var capture = Create();
        var failed = false;
        capture.Failed += _ => failed = true;
        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 2, "audio");

        var result = await capture.SwitchToAsync(CaptureMode.Mix);

        Assert.Equal(CaptureSwitchOutcome.Refused, result.Outcome);
        Assert.Equal(CaptureMode.Mic, result.Active);
        Assert.Contains("not available", result.Error!.Message);
        Drain(capture);
        await WaitUntil(() => _buffer.Count >= 2, "the microphone to go on");
        Assert.All(Drain(capture), tag => Assert.Equal(1f, tag));   // no marker, the same source goes on
        Assert.False(failed);
        Assert.False(_factory.Sources[0].Finished);
        capture.Stop();
    }

    [Fact]
    public async Task SwitchTo_ASourceThatFailsToStart_ShouldGoBackToThePreviousSource()
    {
        _factory.FailAtStart.Add(CaptureMode.Loopback);
        using var capture = Create();
        var failed = false;
        capture.Failed += _ => failed = true;
        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 2, "audio");

        var result = await capture.SwitchToAsync(CaptureMode.Loopback);
        await WaitUntil(() => _buffer.Count >= 6, "audio from the restored microphone");

        Assert.Equal(CaptureSwitchOutcome.Refused, result.Outcome);
        Assert.Equal(CaptureMode.Mic, result.Active);
        Assert.Contains("could not be started", result.Error!.Message);
        Assert.Equal(CaptureMode.Mic, capture.Mode);
        Assert.False(failed);

        // Three sources were made: the microphone, the system audio that failed, a new microphone.
        Assert.Equal([CaptureMode.Mic, CaptureMode.Loopback, CaptureMode.Mic], _factory.Sources.Select(s => s.Mode));
        Assert.True(_factory.Sources[0].Disposed);
        Assert.True(_factory.Sources[1].Disposed);
        Assert.False(_factory.Sources[2].Finished);

        var items = Drain(capture);
        Assert.Equal([Marker(CaptureMode.Loopback), Marker(CaptureMode.Mic)], items.Where(i => i < 0));
        Assert.DoesNotContain(2f, items);
        capture.Stop();
    }

    [Fact]
    public async Task Failed_ShouldReportTheCurrentSource()
    {
        using var capture = new SwitchableCapture(
            new DelegateFactory((mode, _) => new FakeAudioSource(mode, 1) { FailAfterBatches = 3 }), 16000, _buffer, _signal);
        var errors = new List<Exception>();
        capture.Failed += errors.Add;

        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => !capture.IsActive, "capture to end");

        var error = Assert.Single(errors);
        Assert.Contains("unplugged", error.Message);
    }

    [Fact]
    public async Task Failed_ShouldNotBeRaisedForASourceThatWasReplaced()
    {
        _factory.ThrowOnStop.Add(CaptureMode.Mic);
        using var capture = Create();
        var errors = new List<Exception>();
        capture.Failed += errors.Add;
        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 2, "audio");

        var result = await capture.SwitchToAsync(CaptureMode.Loopback);
        await Task.Delay(50);

        Assert.Equal(CaptureSwitchOutcome.Switched, result.Outcome);
        Assert.Empty(errors);
        Assert.True(capture.IsActive);
        capture.Stop();
    }

    [Fact]
    public async Task Failed_ShouldBeRaisedWhenTheSourceThatTookOverFailsLater()
    {
        var created = 0;
        using var capture = new SwitchableCapture(
            new DelegateFactory((mode, _) => new FakeAudioSource(mode, ++created) { FailAfterBatches = created == 2 ? 100 : -1 }),
            16000, _buffer, _signal) { StartGrace = TimeSpan.FromMilliseconds(30) };
        var errors = new List<Exception>();
        capture.Failed += errors.Add;
        capture.Start(CaptureMode.Mic);

        var result = await capture.SwitchToAsync(CaptureMode.Loopback);
        await WaitUntil(() => errors.Count > 0, "the late failure");

        Assert.Equal(CaptureSwitchOutcome.Switched, result.Outcome);
        Assert.Contains("Loopback", errors[0].Message);
    }

    [Fact]
    public async Task IsActive_ShouldStayTrueThroughoutASwitch()
    {
        _factory.StopDelay = TimeSpan.FromMilliseconds(150);   // the microphone takes its time to close
        using var capture = Create();
        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 2, "audio");

        var sawInactive = false;
        using var stop = new CancellationTokenSource();
        var watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                if (!capture.IsActive)
                    sawInactive = true;
        });

        var result = await capture.SwitchToAsync(CaptureMode.Loopback);
        stop.Cancel();
        await watcher;

        Assert.Equal(CaptureSwitchOutcome.Switched, result.Outcome);
        Assert.False(sawInactive, "the consumer must not see the gap between the sources as the end of capture");
        capture.Stop();
    }

    [Fact]
    public async Task Stop_DuringASwitch_ShouldStopTheNewSourceToo()
    {
        _factory.StopDelay = TimeSpan.FromMilliseconds(100);
        using var capture = Create();
        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 2, "audio");

        var switching = capture.SwitchToAsync(CaptureMode.Loopback);
        await Task.Delay(30);
        capture.Stop();
        await switching;

        await WaitUntil(() => !capture.IsActive, "capture to end");
        Assert.All(_factory.Sources, source => Assert.True(source.Finished || !source.Started));
    }

    [Fact]
    public async Task SwitchTo_SeveralTimesAtOnce_ShouldEndOnTheLastRequestedSource()
    {
        using var capture = Create();
        capture.Start(CaptureMode.Mic);
        await WaitUntil(() => _buffer.Count >= 2, "audio");

        var results = await Task.WhenAll(
            capture.SwitchToAsync(CaptureMode.Loopback),
            capture.SwitchToAsync(CaptureMode.Mix),
            capture.SwitchToAsync(CaptureMode.Mic));

        Assert.All(results, r => Assert.Equal(CaptureSwitchOutcome.Switched, r.Outcome));
        Assert.Equal(CaptureMode.Mic, capture.Mode);
        var running = _factory.Sources.Where(s => s.Started && !s.Finished).ToList();
        Assert.Single(running);
        Assert.Equal(CaptureMode.Mic, running[0].Mode);
        capture.Stop();
    }

    [Fact]
    public void IsSwitchMarker_ShouldNotMistakeAudioForAMarker()
    {
        using var capture = Create();

        Assert.False(capture.IsSwitchMarker([0.1f, 0.2f], out _));
    }

    private sealed class DelegateFactory(Func<CaptureMode, int, IAudioSource> create) : IAudioSourceFactory
    {
        public IAudioSource Create(CaptureMode mode, int sampleRate) => create(mode, sampleRate);
    }
}
