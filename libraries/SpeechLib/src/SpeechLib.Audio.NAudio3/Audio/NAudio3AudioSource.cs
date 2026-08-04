using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeechLib.Models;
using System.Buffers;

namespace SpeechLib.Audio;

/// <summary>
/// Windows capture provider built against NAudio 3 preview.
/// The provider keeps the same batched float contract as the stable NAudio provider,
/// so switching providers does not change recognizer allocation behavior.
/// </summary>
public sealed class NAudio3AudioSource : IAudioSource
{
    private const int DrainIntervalMilliseconds = 100;
    private readonly CaptureMode _mode;
    private readonly int _targetRate;
    private CaptureState? _activeState;

    public static AudioLevelMeter AudioLevelMeter { get; } = new();

    private static float _micVolume = 1.0f;
    private static float _loopbackVolume = 1.0f;

    public static float MicVolume
    {
        get => _micVolume;
        set => _micVolume = Math.Clamp(value, 0f, 1f);
    }

    public static float LoopbackVolume
    {
        get => _loopbackVolume;
        set => _loopbackVolume = Math.Clamp(value, 0f, 1f);
    }

    public NAudio3AudioSource(CaptureMode mode, int targetRate)
    {
        if (mode is CaptureMode.File)
            throw new ArgumentException("A live capture mode is required.", nameof(mode));
        if (targetRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRate));

        _mode = mode;
        _targetRate = targetRate;
    }

    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(state);

        if (Interlocked.CompareExchange(ref _activeState, state, null) is not null)
            throw new InvalidOperationException("Audio capture is already running.");

        try
        {
            using var loopback = CreateLoopback(state, signal);
            using var microphone = CreateMicrophone(state, signal);

            if (loopback is null && microphone is null)
                throw new InvalidOperationException($"Capture mode '{_mode}' has no configured source.");

            loopback?.StartRecording();
            microphone?.StartRecording();

            var loopbackSource = loopback is null ? null : CreateNormalizedProvider(loopback.Buffer);
            var microphoneSource = microphone is null ? null : CreateNormalizedProvider(microphone.Buffer);
            var readBuffer = new float[4096];

            try
            {
                while (state.IsRunning)
                {
                    state.Wait(DrainIntervalMilliseconds);
                    if (!state.IsRunning)
                        break;

                    DrainAndPublish(loopbackSource, microphoneSource, readBuffer, buffer, signal);
                }

                DrainAndPublish(loopbackSource, microphoneSource, readBuffer, buffer, signal);
            }
            finally
            {
                state.Stop();
                loopback?.StopRecording();
                microphone?.StopRecording();
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeState, null, state);
        }
    }

    public void Dispose()
    {
        _activeState?.Stop();
    }

    private CaptureHandle? CreateLoopback(CaptureState state, ManualResetEventSlim signal) =>
        _mode is CaptureMode.Loopback or CaptureMode.Mix
            ? CaptureHandle.CreateLoopback(state, signal)
            : null;

    private CaptureHandle? CreateMicrophone(CaptureState state, ManualResetEventSlim signal) =>
        _mode is CaptureMode.Mic or CaptureMode.Mix
            ? CaptureHandle.CreateMicrophone(state, signal)
            : null;

    private ISampleProvider CreateNormalizedProvider(BufferedWaveProvider provider)
    {
        ISampleProvider source = provider.ToSampleProvider();
        if (provider.WaveFormat.Channels > 1)
            source = source.ToMono();
        if (provider.WaveFormat.SampleRate != _targetRate)
            source = new WdlResamplingSampleProvider(source, _targetRate);
        return source;
    }

    private static void DrainAndPublish(
        ISampleProvider? loopback,
        ISampleProvider? microphone,
        float[] readBuffer,
        ConcurrentQueueWrapper buffer,
        ManualResetEventSlim signal)
    {
        float[]? loopbackSamples = null;
        float[]? microphoneSamples = null;
        try
        {
            var loopbackCount = Drain(loopback, readBuffer, ref loopbackSamples);
            var microphoneCount = Drain(microphone, readBuffer, ref microphoneSamples);
            var count = Math.Max(loopbackCount, microphoneCount);
            if (count == 0)
                return;

            var batch = new float[count];
            for (var index = 0; index < count; index++)
            {
                if (index < loopbackCount)
                    batch[index] += loopbackSamples![index] * 0.5f * LoopbackVolume;
                if (index < microphoneCount)
                    batch[index] += microphoneSamples![index] * 0.6f * MicVolume;
            }

            buffer.Enqueue(batch);
            AudioLevelMeter.Publish(batch);
            signal.Set();
        }
        finally
        {
            Return(loopbackSamples);
            Return(microphoneSamples);
        }
    }

    private static int Drain(ISampleProvider? source, float[] readBuffer, ref float[]? samples)
    {
        if (source is null)
            return 0;

        var count = 0;
        int read;
        while ((read = source.Read(readBuffer.AsSpan())) > 0)
        {
            EnsureCapacity(ref samples, count + read, count);
            readBuffer.AsSpan(0, read).CopyTo(samples!.AsSpan(count));
            count += read;
        }

        return count;
    }

    private static void EnsureCapacity(ref float[]? samples, int required, int count)
    {
        if (samples is not null && samples.Length >= required)
            return;

        var capacity = samples is null ? required : Math.Max(required, samples.Length * 2);
        var replacement = ArrayPool<float>.Shared.Rent(capacity);
        if (samples is not null)
        {
            samples.AsSpan(0, count).CopyTo(replacement);
            ArrayPool<float>.Shared.Return(samples);
        }

        samples = replacement;
    }

    private static void Return(float[]? samples)
    {
        if (samples is not null)
            ArrayPool<float>.Shared.Return(samples);
    }

    private sealed class CaptureHandle : IDisposable
    {
        private readonly WaveIn? _microphone;
        private readonly WasapiRecorder? _loopback;

        private CaptureHandle(WaveIn microphone)
        {
            _microphone = microphone;
            Buffer = CreateBuffer(microphone.WaveFormat);
        }

        private CaptureHandle(WasapiRecorder loopback)
        {
            _loopback = loopback;
            Buffer = CreateBuffer(loopback.WaveFormat);
        }

        public BufferedWaveProvider Buffer { get; }

        private static BufferedWaveProvider CreateBuffer(WaveFormat format) =>
            new(format, TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

        public static CaptureHandle CreateMicrophone(CaptureState state, ManualResetEventSlim signal)
        {
            var handle = new CaptureHandle(new WaveIn { WaveFormat = new WaveFormat(16000, 16, 1) });
            handle._microphone!.DataAvailable += (_, args) =>
            {
                if (state.IsRunning)
                    handle.Buffer.AddSamples(args.BufferSpan[..args.BytesRecorded]);
            };
            handle._microphone.RecordingStopped += (_, _) =>
            {
                state.Stop();
                signal.Set();
            };
            return handle;
        }

        public static CaptureHandle CreateLoopback(CaptureState state, ManualResetEventSlim signal)
        {
            var recorder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithSharedMode()
                .WithPollingSync()
                .Build();
            var handle = new CaptureHandle(recorder);
            handle._loopback!.DataAvailable += (audio, _, _, _) =>
            {
                if (state.IsRunning)
                    handle.Buffer.AddSamples(audio);
            };
            handle._loopback.RecordingStopped += (_, _) =>
            {
                state.Stop();
                signal.Set();
            };
            return handle;
        }

        public void StartRecording()
        {
            _microphone?.StartRecording();
            _loopback?.StartRecording();
        }

        public void StopRecording()
        {
            _microphone?.StopRecording();
            _loopback?.StopRecording();
        }

        public void Dispose()
        {
            _microphone?.Dispose();
            _loopback?.Dispose();
        }
    }
}
