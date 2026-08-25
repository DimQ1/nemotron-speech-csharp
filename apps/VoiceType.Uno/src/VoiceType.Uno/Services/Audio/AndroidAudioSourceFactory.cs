// Android microphone capture via AudioRecord.
// Only compiled for the net10.0-android head — on desktop/Windows heads the
// PulseAudio / NAudio factories are used instead. Loopback/Mix are not
// available on Android without elevated privileges; Mic is the only mode.
#if __ANDROID__

using Android.Media;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace VoiceType.Uno.Services.Audio;

/// <summary>
/// Android capture provider backed by <see cref="AudioRecord"/>.
/// Requires the RECORD_AUDIO runtime permission; the app must request it
/// before starting capture.
/// </summary>
public sealed class AndroidAudioSourceFactory : IAudioSourceFactory
{
    public IAudioSource Create(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic => new AndroidAudioSource(sampleRate),
        CaptureMode.Loopback => throw new InvalidOperationException(
            "System audio loopback is not available on Android without elevated privileges."),
        CaptureMode.Mix => throw new InvalidOperationException(
            "Mic + system mix is not available on Android without elevated privileges."),
        _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source.")
    };
}

internal sealed class AndroidAudioSource : IAudioSource
{
    private const int FramesPerRead = 1024;

    private readonly int _sampleRate;

    public AndroidAudioSource(int sampleRate) => _sampleRate = sampleRate;

    public int SourceSampleRate => _sampleRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var minBufferSize = AudioRecord.GetMinBufferSize(
            _sampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        if (minBufferSize <= 0)
            throw new InvalidOperationException("AudioRecord buffer size could not be determined.");

        var bufferSize = Math.Max(minBufferSize, FramesPerRead * sizeof(short) * 4);
        var recorder = new AudioRecord(
            AudioSource.Mic,
            _sampleRate,
            ChannelIn.Mono,
            Encoding.Pcm16bit,
            bufferSize);

        try
        {
            if (recorder.State == State.Uninitialized)
                throw new InvalidOperationException(
                    "AudioRecord failed to initialize — RECORD_AUDIO permission may be missing.");

            recorder.StartRecording();
            if (recorder.RecordingState == RecordState.Stopped)
                throw new InvalidOperationException("AudioRecord did not start recording.");

            var pcm = new short[FramesPerRead];
            while (state.IsRunning)
            {
                var read = recorder.Read(pcm, 0, pcm.Length);
                if (read <= 0)
                {
                    // Transient read error; avoid a tight spin.
                    state.Wait(10);
                    continue;
                }

                var samples = new float[read];
                for (var i = 0; i < read; i++)
                    samples[i] = pcm[i] / 32768f;

                buffer.Enqueue(samples);
                signal.Set();
            }
        }
        finally
        {
            try { recorder.Stop(); } catch { /* ignore stop errors */ }
            recorder.Release();
            recorder.Dispose();
        }
    }

    public void Dispose()
    {
    }
}

#endif
