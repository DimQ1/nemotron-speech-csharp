// iOS microphone capture via AVAudioEngine.
// Only compiled for the net10.0-ios head. iOS gives an app no access to system audio, so Mic is the only mode.
#if __IOS__

using AVFoundation;
using Foundation;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using System.Runtime.InteropServices;

namespace VoiceType.Uno.Services.Audio;

/// <summary>
/// iOS capture provider backed by <see cref="AVAudioEngine"/>. The first capture asks for the microphone permission
/// (<c>NSMicrophoneUsageDescription</c> in Platforms/iOS/Info.plist).
/// </summary>
public sealed class IosAudioSourceFactory : IAudioSourceFactory
{
    public IAudioSource Create(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic => new IosAudioSource(sampleRate),
        CaptureMode.Loopback => throw new InvalidOperationException(
            "System audio loopback is not available on iOS."),
        CaptureMode.Mix => throw new InvalidOperationException(
            "Mic + system mix is not available on iOS."),
        _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source.")
    };
}

internal sealed class IosAudioSource : IAudioSource
{
    // Frames the input node hands over per tap callback (the hardware rate, normally 48 kHz: ~85 ms).
    private const uint TapFrames = 4096;
    private static readonly TimeSpan PermissionTimeout = TimeSpan.FromSeconds(60);

    private readonly int _sampleRate;

    public IosAudioSource(int sampleRate) => _sampleRate = sampleRate;

    public int SourceSampleRate => _sampleRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var session = AVAudioSession.SharedInstance();
        EnsureMicrophonePermission(session);

        session.SetCategory(
            AVAudioSessionCategory.PlayAndRecord,
            AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetooth,
            out var categoryError);
        if (categoryError is not null)
            throw new InvalidOperationException($"Audio session category failed: {categoryError.LocalizedDescription}");
        session.SetActive(true, out var activeError);
        if (activeError is not null)
            throw new InvalidOperationException($"Audio session activation failed: {activeError.LocalizedDescription}");

        using var engine = new AVAudioEngine();
        var input = engine.InputNode;
        var inputFormat = input.GetBusOutputFormat(0);
        if (inputFormat.SampleRate <= 0 || inputFormat.ChannelCount == 0)
            throw new InvalidOperationException("The microphone is not available (no input format).");

        // The converter does the sample-rate change and the mixdown to mono in one step.
        using var target = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, _sampleRate, 1, false);
        using var converter = new AVAudioConverter(inputFormat, target);
        var ratio = _sampleRate / inputFormat.SampleRate;

        input.InstallTapOnBus(0, TapFrames, inputFormat, (inBuffer, _) =>
        {
            try
            {
                var capacity = (uint)Math.Ceiling(inBuffer.FrameLength * ratio) + 32;
                using var outBuffer = new AVAudioPcmBuffer(target, capacity);
                var supplied = false;
                converter.ConvertToBuffer(outBuffer, out var convertError, (uint _, out AVAudioConverterInputStatus status) =>
                {
                    if (supplied)
                    {
                        status = AVAudioConverterInputStatus.NoDataNow;
                        return null!;
                    }

                    supplied = true;
                    status = AVAudioConverterInputStatus.HaveData;
                    return inBuffer;
                });
                if (convertError is not null || outBuffer.FrameLength == 0)
                    return;

                // FloatChannelData is a float** : the first pointer is channel 0 of the mono buffer.
                var samples = new float[(int)outBuffer.FrameLength];
                Marshal.Copy(Marshal.ReadIntPtr(outBuffer.FloatChannelData), samples, 0, samples.Length);
                buffer.Enqueue(samples);
                signal.Set();
            }
            catch
            {
                // A failed buffer must not take the audio thread down; the next callback tries again.
            }
        });

        engine.Prepare();
        if (!engine.StartAndReturnError(out var startError))
            throw new InvalidOperationException($"AVAudioEngine did not start: {startError?.LocalizedDescription}");

        try
        {
            while (state.IsRunning)
                state.Wait(100);
        }
        finally
        {
            try { input.RemoveTapOnBus(0); } catch { /* ignore */ }
            try { engine.Stop(); } catch { /* ignore */ }
            try { session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _); } catch { /* ignore */ }
        }
    }

    private static void EnsureMicrophonePermission(AVAudioSession session)
    {
        var granted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA1422 // AVAudioApplication.RequestRecordPermission replaces it from iOS 17; this one works on iOS 15 and 16 too.
        session.RequestRecordPermission(allowed => granted.TrySetResult(allowed));
#pragma warning restore CA1422
        if (!granted.Task.Wait(PermissionTimeout) || !granted.Task.Result)
            throw new InvalidOperationException(
                "Microphone permission was not granted. Allow VoiceType to use the microphone in Settings > Privacy.");
    }

    public void Dispose()
    {
    }
}

#endif
