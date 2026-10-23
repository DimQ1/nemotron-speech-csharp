using SpeechLib;
using SpeechLib.Audio;
using VoiceType.Uno.Services.Audio;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests;

public class HelperAudioSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueueWrapper _buffer = new(capacity: 10_000);
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly CaptureState _state = new();

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

    private Task Run(IAudioSource source) =>
        Task.Factory.StartNew(() => source.Start(_buffer, _signal, _state), TaskCreationOptions.LongRunning);

    private readonly List<float> _received = new();

    /// <summary>Everything the capture published so far (kept across calls).</summary>
    private List<float> Received()
    {
        while (_buffer.TryDequeue(out var batch))
            _received.AddRange(batch);
        return _received;
    }

    private static HelperAudioSource Source(params (string Name, FakePcmHelper Helper)[] helpers) =>
        new(16000, helpers.Select(h => (h.Name, (Func<IPcmHelper>)(() => h.Helper))).ToList());

    [Fact]
    public void Decoder_ShouldReadTheRateAndTheSamples_WhateverTheReadSizes()
    {
        var bytes = FakePcmHelper.Header(48000).Concat(FakePcmHelper.Samples(0.5f, -0.25f, 1f)).ToArray();
        var decoder = new HelperPcmDecoder();

        var samples = new List<float>();
        foreach (var b in bytes)
            samples.AddRange(decoder.Push([b]).SelectMany(c => c.Samples));

        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal([0.5f, -0.25f, 1f], samples);
    }

    [Fact]
    public void Decoder_ShouldStartANewChunk_WhenTheRateChanges()
    {
        var bytes = FakePcmHelper.Header(48000)
            .Concat(FakePcmHelper.Samples(0.1f, 0.2f))
            .Concat(FakePcmHelper.RateChange(44100))
            .Concat(FakePcmHelper.Samples(0.3f))
            .ToArray();

        var chunks = new HelperPcmDecoder().Push(bytes);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(48000, chunks[0].SampleRate);
        Assert.Equal(new[] { 0.1f, 0.2f }, chunks[0].Samples);
        Assert.Equal(44100, chunks[1].SampleRate);
        Assert.Equal(new[] { 0.3f }, chunks[1].Samples);
    }

    [Fact]
    public void Decoder_ShouldReject_AnImpossibleRate()
    {
        Assert.Throws<InvalidDataException>(() => new HelperPcmDecoder().Push(FakePcmHelper.Header(-5)));
    }

    [Fact]
    public async Task Start_ShouldDeliverTheHelperAudio_AtTheRecognizerRate()
    {
        var mic = new FakePcmHelper();
        var capture = Run(Source(("mic", mic)));

        mic.Write(FakePcmHelper.Header(48000));
        mic.Write(FakePcmHelper.Constant(0.5f, 24000));   // 0.5 s
        await WaitUntil(() => Received().Count >= 7800, "0.5 s of audio at 16 kHz");

        _state.Stop();
        await capture.WaitAsync(Timeout);

        // 24000 samples at 48 kHz are 8000 at 16 kHz, less the filter delay still inside the resampler.
        Assert.InRange(Received().Count, 7800, 8000);
        Assert.InRange(Received()[4000], 0.49f, 0.51f);
        Assert.True(mic.StopRequested);
        Assert.True(mic.Disposed);
    }

    [Fact]
    public async Task Start_ShouldFollowARateChange_OfTheHelper()
    {
        var mic = new FakePcmHelper();
        var capture = Run(Source(("mic", mic)));

        mic.Write(FakePcmHelper.Header(16000));
        mic.Write(FakePcmHelper.Constant(0.25f, 1600));
        mic.Write(FakePcmHelper.RateChange(48000));
        mic.Write(FakePcmHelper.Constant(0.25f, 4800));
        await WaitUntil(() => Received().Count >= 1600 + 1550, "0.1 s at each rate");

        _state.Stop();
        await capture.WaitAsync(Timeout);
        Assert.InRange(Received().Count, 1600 + 1550, 3200);
    }

    [Fact]
    public async Task Start_ShouldFailWithTheHelperMessage_WhenItEndsBeforeAnyAudio()
    {
        var mic = new FakePcmHelper();
        var capture = Run(Source(("mic", mic)));

        mic.Exit(2, "microphone: starting\nMicrophone access is off for VoiceType. System Settings > Privacy & Security > Microphone: turn on VoiceType.\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => capture.WaitAsync(Timeout));
        Assert.StartsWith("Microphone access is off for VoiceType.", error.Message);
        Assert.True(mic.Disposed);
    }

    [Fact]
    public async Task Start_ShouldFail_WhenTheOnlyHelperStopsLater()
    {
        var system = new FakePcmHelper();
        var capture = Run(Source(("system", system)));
        system.Write(FakePcmHelper.Header(16000));
        system.Write(FakePcmHelper.Constant(0.1f, 1600));
        await WaitUntil(() => Received().Count >= 1600, "audio before the failure");

        system.Exit(2, "System audio could not be started (-1).");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => capture.WaitAsync(Timeout));
        Assert.Equal("System audio could not be started (-1).", error.Message);
    }

    [Fact]
    public async Task Mix_ShouldSumBothHelpers()
    {
        var mic = new FakePcmHelper();
        var system = new FakePcmHelper();
        var capture = Run(Source(("mic", mic), ("system", system)));

        mic.Write(FakePcmHelper.Header(16000));
        system.Write(FakePcmHelper.Header(16000));
        mic.Write(FakePcmHelper.Constant(0.25f, 1600));
        system.Write(FakePcmHelper.Constant(0.5f, 1600));
        await WaitUntil(() => Received().Count >= 1600, "the mixed block");

        _state.Stop();
        await capture.WaitAsync(Timeout);
        Assert.Equal(1600, Received().Count);
        Assert.All(Received(), s => Assert.Equal(0.75f, s, 0.0001f));
    }

    [Fact]
    public async Task Mix_ShouldFail_WhenOneHelperIsRefusedAtTheStart()
    {
        var mic = new FakePcmHelper();
        var system = new FakePcmHelper();
        var capture = Run(Source(("mic", mic), ("system", system)));
        mic.Write(FakePcmHelper.Header(16000));
        mic.Write(FakePcmHelper.Constant(0.25f, 1600));

        system.Exit(2, "System audio could not be captured (-1). System Settings > Privacy & Security > Screen & System Audio Recording: allow VoiceType.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => capture.WaitAsync(Timeout));
        Assert.StartsWith("System audio could not be captured", error.Message);
        Assert.True(mic.StopRequested);
    }

    [Fact]
    public async Task Mix_ShouldGoOnWithTheOtherHelper_WhenOneStopsLater()
    {
        var mic = new FakePcmHelper();
        var system = new FakePcmHelper();
        var capture = Run(Source(("mic", mic), ("system", system)));
        mic.Write(FakePcmHelper.Header(16000));
        system.Write(FakePcmHelper.Header(16000));
        mic.Write(FakePcmHelper.Constant(0.25f, 1600));
        system.Write(FakePcmHelper.Constant(0.5f, 1600));
        await WaitUntil(() => Received().Count >= 1600, "the mixed block");

        system.Exit(2, "System audio stopped.");
        await Task.Delay(100);
        mic.Write(FakePcmHelper.Constant(0.25f, 1600));
        await WaitUntil(() => Received().Count >= 3200, "the microphone alone");

        Assert.False(capture.IsCompleted);
        Assert.All(Received().Skip(1600), s => Assert.Equal(0.25f, s, 0.0001f));
        _state.Stop();
        await capture.WaitAsync(Timeout);
    }

    [UnixFact]
    public async Task ProcessHelper_ShouldStream_UntilItsInputIsClosed()
    {
        // 16000 Hz header, two samples of 0.5, then wait for stdin to close like voicetype-audio does.
        var script = WriteScript("""
            printf '\200\076\000\000\000\000\000\077\000\000\000\077'
            cat > /dev/null
            """);
        var capture = Run(new HelperAudioSource(16000, [("mic", () => new ProcessPcmHelper(script, "mic"))]));

        await WaitUntil(() => Received().Count >= 2, "the two samples");
        _state.Stop();
        await capture.WaitAsync(Timeout);

        Assert.Equal([0.5f, 0.5f], Received());
    }

    [UnixFact]
    public async Task ProcessHelper_ShouldFailWithItsLastErrorLine()
    {
        var script = WriteScript("""
            echo "microphone: starting" >&2
            echo "Microphone access is off for VoiceType." >&2
            exit 2
            """);
        var capture = Run(new HelperAudioSource(16000, [("mic", () => new ProcessPcmHelper(script, "mic"))]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => capture.WaitAsync(Timeout));
        Assert.Equal("Microphone access is off for VoiceType.", error.Message);
    }

    private static string WriteScript(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"voicetype-audio-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
