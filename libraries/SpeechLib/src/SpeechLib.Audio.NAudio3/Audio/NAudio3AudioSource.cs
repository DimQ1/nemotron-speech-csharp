using NAudio.CoreAudioApi;
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

    /// <summary>Level meter for the microphone channel (pre-mix gain).</summary>
    public static AudioLevelMeter MicLevelMeter { get; } = new();

    /// <summary>Level meter for the loopback channel (pre-mix gain).</summary>
    public static AudioLevelMeter LoopbackLevelMeter { get; } = new();

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

        CaptureHandle? loopback = null;
        CaptureHandle? microphone = null;
        try
        {
            if (_mode is CaptureMode.Loopback or CaptureMode.Mix)
            {
                try
                {
                    loopback = CreateLoopback(state, signal);
                }
                catch (Exception ex)
                {
                    if (_mode == CaptureMode.Mix)
                    {
                        // Missing render device is not fatal in Mix mode — degrade to mic-only.
                        Console.Error.WriteLine($"[capture] Loopback unavailable — continuing with microphone only: {ex.Message}");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "No audio render device is available for system-audio (loopback) capture. " +
                            "Start playing audio, or run with a microphone instead.", ex);
                    }
                }
            }

            if (_mode is CaptureMode.Mic or CaptureMode.Mix)
            {
                try
                {
                    microphone = CreateMicrophone(state, signal);
                }
                catch (Exception ex)
                {
                    if (_mode == CaptureMode.Mix)
                    {
                        Console.Error.WriteLine($"[capture] Microphone unavailable — continuing with system audio only: {ex.Message}");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "The microphone could not be started. It may be in use by another application or disabled.", ex);
                    }
                }
            }

            if (loopback is null && microphone is null)
                throw new InvalidOperationException(
                    "No audio source could be started. Check your microphone and system-audio settings.");

            TryStart(loopback, "Loopback", signal, ref loopback);
            TryStart(microphone, "Microphone", signal, ref microphone);

            if (loopback is null && microphone is null)
                throw new InvalidOperationException(
                    "No audio source could be started. Check your microphone and system-audio settings.");

            var loopbackSource = loopback?.Buffer;
            var microphoneSource = microphone?.Buffer;
            var readBuffer = new float[4096];

            try
            {
                while (state.IsRunning)
                {
                    state.Wait(DrainIntervalMilliseconds);
                    if (!state.IsRunning)
                        break;

                    DrainAndPublish(loopbackSource, microphoneSource, readBuffer, buffer, signal,
                        loopback?.Buffer, microphone?.Buffer);
                }

                DrainAndPublish(loopbackSource, microphoneSource, readBuffer, buffer, signal,
                    loopback?.Buffer, microphone?.Buffer);
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
            loopback?.Dispose();
            microphone?.Dispose();
            Interlocked.CompareExchange(ref _activeState, null, state);
        }
    }

    /// <summary>
    /// Starts one capture handle, degrading Mix mode to the other source when the
    /// start call fails. Non-Mix failures are rethrown with an actionable message.
    /// </summary>
    private void TryStart(CaptureHandle? handle, string what, ManualResetEventSlim signal, ref CaptureHandle? target)
    {
        if (handle is null)
            return;

        try
        {
            handle.StartRecording();
        }
        catch (Exception ex)
        {
            if (_mode == CaptureMode.Mix)
            {
                Console.Error.WriteLine($"[capture] {what} failed to start — continuing with the other source: {ex.Message}");
                handle.Dispose();
                target = null;
            }
            else
            {
                throw new InvalidOperationException($"{what} capture could not start.", ex);
            }
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

    private static void DrainAndPublish(
        BufferedWaveProvider? loopback,
        BufferedWaveProvider? microphone,
        float[] readBuffer,
        ConcurrentQueueWrapper buffer,
        ManualResetEventSlim signal,
        BufferedWaveProvider? loopbackBuf = null,
        BufferedWaveProvider? micBuf = null)
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

            // Per-channel levels (pre-mix gain) so the mixer UI can show each source
            if (microphoneCount > 0)
                MicLevelMeter.PublishIfActive(microphoneSamples.AsSpan(0, microphoneCount));
            if (loopbackCount > 0)
                LoopbackLevelMeter.PublishIfActive(loopbackSamples.AsSpan(0, loopbackCount));

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

    /// <summary>
    /// Drain a <see cref="BufferedWaveProvider"/> directly to mono float samples at the
    /// target rate. Replaces the WdlResamplingSampleProvider pipeline, which stalled on the
    /// loopback stream (48 kHz float stereo) and returned almost no samples.
    /// Steps: raw bytes → PCM/float decode → stereo average to mono → linear downsample.
    /// </summary>
    private static int Drain(BufferedWaveProvider? source, float[] readBuffer, ref float[]? samples)
    {
        if (source is null)
            return 0;

        var fmt = source.WaveFormat;
        var bytes = source.BufferedBytes;
        var bytesPerFrame = fmt.BlockAlign;
        var frames = bytes / bytesPerFrame;
        if (frames <= 0)
            return 0;

        var raw = new byte[frames * bytesPerFrame];
        var read = source.Read(raw.AsSpan(0, raw.Length));
        frames = read / bytesPerFrame;
        if (frames <= 0)
            return 0;

        var outCount = (int)((long)frames * 16000 / fmt.SampleRate);
        if (outCount <= 0)
            return 0;

        EnsureCapacity(ref samples, outCount, 0);
        var dst = samples!;
        var channels = fmt.Channels;
        var step = (double)fmt.SampleRate / 16000.0;

        for (var o = 0; o < outCount; o++)
        {
            var srcFrame = (int)(o * step);
            if (srcFrame >= frames)
                srcFrame = frames - 1;

            var offset = srcFrame * bytesPerFrame;
            float sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += ReadSample(raw, offset + c * (fmt.BitsPerSample / 8), fmt);

            dst[o] = sum / channels;
        }

        return outCount;
    }

    /// <summary>Decode a single PCM16 or IEEE-float32 sample.</summary>
    private static float ReadSample(byte[] raw, int offset, WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            return BitConverter.ToSingle(raw, offset);

        // 16-bit PCM (both mic WaveIn and typical capture formats)
        return BitConverter.ToInt16(raw, offset) / 32768f;
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
        private readonly WasapiCapture? _microphone;
        private readonly WasapiLoopbackCapture? _loopback;

        private CaptureHandle(WasapiCapture microphone)
        {
            _microphone = microphone;
            Buffer = CreateBuffer(microphone.WaveFormat);
        }

        private CaptureHandle(WasapiLoopbackCapture loopback)
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
            // WasapiCapture (shared mode) replaces WaveIn/WinMM so the whole
            // capture path stays portable (NAudio.Wasapi targets net9.0). It
            // records the default capture endpoint; the Drain step resamples to
            // 16 kHz mono regardless of the device mix format.
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            var capture = new WasapiCapture(device);
            var handle = new CaptureHandle(capture);
            handle._microphone!.DataAvailable += (_, args) =>
            {
                if (state.IsRunning)
                    handle.Buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
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
            // WasapiLoopbackCapture is the proven loopback path (same as the NAudio2 provider):
            // the WasapiRecorder builder API in the NAudio 3 preview never raised DataAvailable
            // in this scenario, so loopback stayed silent (see capture-diag.log investigation).
            var capture = new WasapiLoopbackCapture();
            var handle = new CaptureHandle(capture);
            handle._loopback!.DataAvailable += (_, args) =>
            {
                if (state.IsRunning)
                    handle.Buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
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
