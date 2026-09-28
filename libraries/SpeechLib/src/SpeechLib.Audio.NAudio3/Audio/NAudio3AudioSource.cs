using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpeechLib.Models;
using System.Runtime.Versioning;

namespace SpeechLib.Audio;

/// <summary>
/// Windows capture provider built against NAudio 3.
/// The provider keeps the same batched float contract as the stable NAudio provider,
/// so switching providers does not change recognizer allocation behavior.
/// <para>
/// Each device is captured in its native shared-mode mix format (typically 48 kHz float
/// stereo), decoded to mono and converted to the recognizer rate with a stateful
/// anti-aliased <see cref="StreamingResampler"/>. Mic and loopback are then summed with
/// their user gains and published as one batch every <see cref="DrainIntervalMilliseconds"/>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NAudio3AudioSource : IAudioSource
{
    /// <summary>How often buffered device audio is drained, mixed and published.</summary>
    private const int DrainIntervalMilliseconds = 50;

    /// <summary>WASAPI buffer length for the microphone; data still arrives per 10 ms period in event mode.</summary>
    private const int MicrophoneBufferMilliseconds = 100;

    /// <summary>Ring capacity per device; drains happen every 50 ms so this only absorbs scheduling hiccups.</summary>
    private static readonly TimeSpan DeviceRingDuration = TimeSpan.FromSeconds(2);

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
                loopback = TryCreate("Loopback", () => CaptureHandle.CreateLoopback(state, _targetRate),
                    "No audio render device is available for system-audio (loopback) capture. " +
                    "Start playing audio, or run with a microphone instead.");

            if (_mode is CaptureMode.Mic or CaptureMode.Mix)
                microphone = TryCreate("Microphone", () => CaptureHandle.CreateMicrophone(state, _targetRate),
                    "The microphone could not be started. It may be in use by another application or disabled.");

            loopback = TryStart(loopback, "Loopback");
            microphone = TryStart(microphone, "Microphone");

            if (loopback is null && microphone is null)
                throw new InvalidOperationException(
                    "No audio source could be started. Check your microphone and system-audio settings.");

            try
            {
                while (state.IsRunning)
                {
                    state.Wait(DrainIntervalMilliseconds);
                    if (!state.IsRunning)
                        break;

                    DrainAndPublish(loopback, microphone, buffer, signal);

                    // A device that stopped on its own (unplugged, format change, driver error)
                    // must not silently end the session: degrade in Mix mode, fail otherwise.
                    loopback = CheckFault(loopback, "Loopback", otherAlive: microphone is not null);
                    microphone = CheckFault(microphone, "Microphone", otherAlive: loopback is not null);
                }

                DrainAndPublish(loopback, microphone, buffer, signal);
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

    public void Dispose()
    {
        _activeState?.Stop();
    }

    /// <summary>Create a handle; a missing device is fatal unless Mix mode still has the other source.</summary>
    private CaptureHandle? TryCreate(string what, Func<CaptureHandle> create, string failureMessage)
    {
        try
        {
            return create();
        }
        catch (Exception ex)
        {
            if (_mode != CaptureMode.Mix)
                throw new InvalidOperationException(failureMessage, ex);

            Console.Error.WriteLine($"[capture] {what} unavailable — continuing with the other source: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Starts one capture handle, degrading Mix mode to the other source when the
    /// start call fails. Non-Mix failures are rethrown with an actionable message.
    /// </summary>
    private CaptureHandle? TryStart(CaptureHandle? handle, string what)
    {
        if (handle is null)
            return null;

        try
        {
            handle.StartRecording();
            return handle;
        }
        catch (Exception ex)
        {
            handle.Dispose();
            if (_mode != CaptureMode.Mix)
                throw new InvalidOperationException($"{what} capture could not start.", ex);

            Console.Error.WriteLine($"[capture] {what} failed to start — continuing with the other source: {ex.Message}");
            return null;
        }
    }

    /// <summary>Handle a device that stopped by itself. Returns the handle to keep using (or null).</summary>
    private CaptureHandle? CheckFault(CaptureHandle? handle, string what, bool otherAlive)
    {
        if (handle is null || !handle.HasStopped)
            return handle;

        var fault = handle.Fault;
        if (_mode == CaptureMode.Mix && otherAlive)
        {
            Console.Error.WriteLine($"[capture] {what} stopped — continuing with the other source: {fault?.Message ?? "device stopped"}");
            handle.Dispose();
            return null;
        }

        throw new InvalidOperationException(
            $"{what} capture stopped unexpectedly. The device may have been disconnected or its format changed.",
            fault);
    }

    private static void DrainAndPublish(
        CaptureHandle? loopback,
        CaptureHandle? microphone,
        ConcurrentQueueWrapper buffer,
        ManualResetEventSlim signal)
    {
        var loopbackSamples = loopback is null ? ReadOnlySpan<float>.Empty : loopback.Drain();
        var microphoneSamples = microphone is null ? ReadOnlySpan<float>.Empty : microphone.Drain();

        var count = CaptureMixer.OutputLength(loopbackSamples.Length, microphoneSamples.Length);
        if (count == 0)
            return;

        // Per-channel levels (pre-mix gain) so the mixer UI can show each source.
        if (microphoneSamples.Length > 0)
            MicLevelMeter.PublishIfActive(microphoneSamples);
        if (loopbackSamples.Length > 0)
            LoopbackLevelMeter.PublishIfActive(loopbackSamples);

        // The batch is handed to the consumer, so it must be a fresh array.
        var batch = new float[count];
        CaptureMixer.Mix(loopbackSamples, LoopbackVolume, microphoneSamples, MicVolume, batch);

        buffer.Enqueue(batch);
        AudioLevelMeter.Publish(batch);
        signal.Set();
    }

// CS0618: WasapiCapture/WasapiLoopbackCapture are deprecated in NAudio 3 preview in
// favour of WasapiRecorderBuilder, but the builder API never raised DataAvailable for
// loopback in this scenario (capture-diag.log investigation), so the proven classes stay.
#pragma warning disable CS0618
    /// <summary>
    /// One captured device: WASAPI stream → thread-safe byte ring (filled on the WASAPI
    /// thread) → decode/downmix/resample on the drain thread into a reusable float buffer.
    /// </summary>
    private sealed class CaptureHandle : IDisposable
    {
        private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

        private readonly WasapiCapture _capture;
        private readonly MMDevice? _device;
        private readonly MMDeviceEnumerator? _enumerator;
        private readonly BufferedWaveProvider _ring;
        private readonly PcmSampleFormat _sampleFormat;
        private readonly int _channels;
        private readonly int _blockAlign;
        private readonly StreamingResampler _resampler;
        private byte[] _raw = Array.Empty<byte>();
        private float[] _mono = Array.Empty<float>();
        private float[] _resampled = Array.Empty<float>();
        private volatile bool _stopRequested;
        private volatile bool _stopped;
        private Exception? _fault;

        private CaptureHandle(WasapiCapture capture, MMDevice? device, MMDeviceEnumerator? enumerator,
            CaptureState state, int targetRate)
        {
            _capture = capture;
            _device = device;
            _enumerator = enumerator;

            var format = capture.WaveFormat;
            _sampleFormat = ToSampleFormat(format);
            _channels = format.Channels;
            _blockAlign = format.BlockAlign;
            _resampler = new StreamingResampler(format.SampleRate, targetRate);
            _ring = new BufferedWaveProvider(format, DeviceRingDuration)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            _capture.DataAvailable += (_, args) =>
            {
                if (state.IsRunning)
                    _ring.AddSamples(args.Buffer, 0, args.BytesRecorded);
            };
            _capture.RecordingStopped += (_, args) =>
            {
                // Our own StopRecording() also lands here (Exception == null) — only an
                // unsolicited stop counts as a fault. The drain loop polls HasStopped.
                if (!_stopRequested)
                    _fault = args.Exception ?? new InvalidOperationException("The capture device stopped delivering audio.");
                _stopped = true;
            };
        }

        /// <summary>True once the WASAPI capture thread has exited without us asking for it.</summary>
        public bool HasStopped => _stopped && !_stopRequested;

        /// <summary>Exception reported by the device when <see cref="HasStopped"/> is true.</summary>
        public Exception? Fault => _fault;

        public static CaptureHandle CreateMicrophone(CaptureState state, int targetRate)
        {
            // WasapiCapture (shared mode) replaces WaveIn/WinMM so the whole capture path
            // stays portable (NAudio.Wasapi targets net9.0). Event-driven mode delivers
            // packets every engine period (10 ms) instead of after a half-buffer sleep.
            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            try
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                var capture = new WasapiCapture(device, useEventSync: true, MicrophoneBufferMilliseconds);
                return new CaptureHandle(capture, device, enumerator, state, targetRate);
            }
            catch
            {
                device?.Dispose();
                enumerator.Dispose();
                throw;
            }
        }

        public static CaptureHandle CreateLoopback(CaptureState state, int targetRate)
        {
            // WasapiLoopbackCapture is the proven loopback path (kept from the previous provider):
            // the WasapiRecorder builder API in the NAudio 3 preview never raised DataAvailable
            // in this scenario, so loopback stayed silent (see capture-diag.log investigation).
            // Loopback streams do not signal the WASAPI event, so this one stays in polling mode.
            var capture = new WasapiLoopbackCapture();
            return new CaptureHandle(capture, null, null, state, targetRate);
        }

        public void StartRecording() => _capture.StartRecording();

        public void StopRecording()
        {
            _stopRequested = true;
            _capture.StopRecording();
        }

        /// <summary>
        /// Drain everything the device delivered since the last call and return it as mono
        /// samples at the target rate. The returned span aliases an internal buffer that is
        /// overwritten by the next call.
        /// </summary>
        public ReadOnlySpan<float> Drain()
        {
            var frames = _ring.BufferedBytes / _blockAlign;
            if (frames <= 0)
                return ReadOnlySpan<float>.Empty;

            var bytes = frames * _blockAlign;
            EnsureCapacity(ref _raw, bytes);
            var read = _ring.Read(_raw.AsSpan(0, bytes));
            frames = read / _blockAlign;
            if (frames <= 0)
                return ReadOnlySpan<float>.Empty;

            EnsureCapacity(ref _mono, frames);
            frames = PcmSampleDecoder.DecodeToMono(_raw.AsSpan(0, frames * _blockAlign), _sampleFormat, _channels, _mono);
            if (_resampler.IsPassThrough)
                return _mono.AsSpan(0, frames);

            EnsureCapacity(ref _resampled, _resampler.MaxOutputCount(frames));
            var produced = _resampler.Process(_mono.AsSpan(0, frames), _resampled);
            return _resampled.AsSpan(0, produced);
        }

        public void Dispose()
        {
            _stopRequested = true;
            _capture.Dispose();
            _device?.Dispose();
            _enumerator?.Dispose();
        }

        private static PcmSampleFormat ToSampleFormat(WaveFormat format)
        {
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                          || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubtype);

            if (isFloat)
            {
                return format.BitsPerSample == 32
                    ? PcmSampleFormat.Float32
                    : throw new NotSupportedException($"Unsupported float sample width: {format.BitsPerSample} bit.");
            }

            return format.BitsPerSample switch
            {
                8 => PcmSampleFormat.Pcm8,
                16 => PcmSampleFormat.Pcm16,
                24 => PcmSampleFormat.Pcm24,
                32 => PcmSampleFormat.Pcm32,
                _ => throw new NotSupportedException($"Unsupported PCM sample width: {format.BitsPerSample} bit.")
            };
        }

        private static void EnsureCapacity<T>(ref T[] array, int required)
        {
            if (array.Length >= required)
                return;
            array = new T[Math.Max(required, array.Length * 2)];
        }
    }
#pragma warning restore CS0618
}
