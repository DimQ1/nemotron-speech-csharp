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

        var loopbacks = new List<CaptureHandle>();
        CaptureHandle? microphone = null;
        try
        {
            if (_mode is CaptureMode.Loopback or CaptureMode.Mix)
                loopbacks = CreateLoopbacks(state);

            if (_mode is CaptureMode.Mic or CaptureMode.Mix)
                microphone = TryCreate("Microphone", () => CaptureHandle.CreateMicrophone(state, _targetRate),
                    "The microphone could not be started. It may be in use by another application or disabled.");

            loopbacks = StartLoopbacks(loopbacks);
            microphone = TryStart(microphone, "Microphone");

            if (loopbacks.Count == 0 && microphone is null)
                throw new InvalidOperationException(
                    "No audio source could be started. Check your microphone and system-audio settings.");

            // One mixer source per device; streams are aligned instead of padded per drain.
            var mixer = new MultiSourceMixer(maxSkewSamples: _targetRate * 150 / 1000);
            var loopbackSources = loopbacks.Select(_ => mixer.AddSource()).ToList();
            var microphoneSource = mixer.AddSource();

            try
            {
                while (state.IsRunning)
                {
                    state.Wait(DrainIntervalMilliseconds);
                    if (!state.IsRunning)
                        break;

                    DrainAndPublish(loopbacks, loopbackSources, microphone, microphoneSource, mixer, buffer, signal);

                    // A device that stopped on its own (unplugged, format change, driver error)
                    // must not silently end the session: keep going while another source is
                    // alive, fail otherwise.
                    for (var i = loopbacks.Count - 1; i >= 0; i--)
                    {
                        var otherAlive = microphone is not null || loopbacks.Count > 1;
                        if (CheckFault(loopbacks[i], "Loopback", otherAlive) is null)
                        {
                            mixer.RemoveSource(loopbackSources[i]);
                            loopbacks.RemoveAt(i);
                            loopbackSources.RemoveAt(i);
                        }
                    }

                    if (microphone is not null && CheckFault(microphone, "Microphone", otherAlive: loopbacks.Count > 0) is null)
                    {
                        mixer.RemoveSource(microphoneSource);
                        microphone = null;
                    }
                }

                DrainAndPublish(loopbacks, loopbackSources, microphone, microphoneSource, mixer, buffer, signal);
            }
            finally
            {
                state.Stop();
                foreach (var loopback in loopbacks)
                    loopback.StopRecording();
                microphone?.StopRecording();
            }
        }
        finally
        {
            foreach (var loopback in loopbacks)
                loopback.Dispose();
            microphone?.Dispose();
            Interlocked.CompareExchange(ref _activeState, null, state);
        }
    }

    /// <summary>
    /// Loopback handles for every distinct default output device. Windows routes media
    /// to the default device and calls (Teams, Zoom, browsers in a call) to the default
    /// communication device; when those differ (monitor speakers vs a headset), capturing
    /// only the default device records silence during a call.
    /// </summary>
    private List<CaptureHandle> CreateLoopbacks(CaptureState state)
    {
        var handles = new List<CaptureHandle>();
        Exception? lastError = null;
        foreach (var deviceId in DefaultRenderDeviceIds())
        {
            try
            {
                handles.Add(CaptureHandle.CreateLoopback(deviceId, state, _targetRate));
            }
            catch (Exception ex)
            {
                lastError = ex;
                Console.Error.WriteLine($"[capture] Loopback device unavailable ({deviceId}): {ex.Message}");
            }
        }

        if (handles.Count == 0 && _mode != CaptureMode.Mix)
            throw new InvalidOperationException(
                "No audio render device is available for system-audio (loopback) capture. " +
                "Start playing audio, or run with a microphone instead.", lastError);

        return handles;
    }

    /// <summary>Default render endpoints for the multimedia and communications roles, without duplicates.</summary>
    private static List<string> DefaultRenderDeviceIds()
    {
        var ids = new List<string>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var role in new[] { Role.Multimedia, Role.Communications })
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                if (!ids.Contains(device.ID, StringComparer.OrdinalIgnoreCase))
                    ids.Add(device.ID);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[capture] No default render device for {role}: {ex.Message}");
            }
        }

        return ids;
    }

    /// <summary>Starts the loopback handles; loopback capture fails only when none of them starts.</summary>
    private List<CaptureHandle> StartLoopbacks(List<CaptureHandle> handles)
    {
        var started = new List<CaptureHandle>();
        Exception? lastError = null;
        foreach (var handle in handles)
        {
            try
            {
                handle.StartRecording();
                started.Add(handle);
            }
            catch (Exception ex)
            {
                lastError = ex;
                handle.Dispose();
                Console.Error.WriteLine($"[capture] Loopback device failed to start: {ex.Message}");
            }
        }

        if (handles.Count > 0 && started.Count == 0 && _mode != CaptureMode.Mix)
            throw new InvalidOperationException("Loopback capture could not start.", lastError);

        return started;
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

    /// <summary><c>SPEECHLIB_CAPTURE_TRACE=1</c> prints per-second capture levels to stderr.</summary>
    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("SPEECHLIB_CAPTURE_TRACE") == "1";

    private static int _traceDrains;

    private static void DrainAndPublish(
        List<CaptureHandle> loopbacks,
        List<int> loopbackSources,
        CaptureHandle? microphone,
        int microphoneSource,
        MultiSourceMixer mixer,
        ConcurrentQueueWrapper buffer,
        ManualResetEventSlim signal)
    {
        // Per-channel levels (pre-mix gain) so the mixer UI can show each source. With
        // several output devices the loudest one is shown for the system-audio channel.
        var loopbackPeak = 0f;
        float[]? loudestLoopback = null;
        for (var i = 0; i < loopbacks.Count; i++)
        {
            var samples = loopbacks[i].Drain();
            if (samples.IsEmpty)
                continue;

            mixer.SetGain(loopbackSources[i], LoopbackVolume);
            mixer.Push(loopbackSources[i], samples);

            var peak = Peak(samples);
            if (loudestLoopback is null || peak > loopbackPeak)
            {
                loopbackPeak = peak;
                loudestLoopback = samples.ToArray();
            }
        }

        if (loudestLoopback is not null)
            LoopbackLevelMeter.PublishIfActive(loudestLoopback);

        if (microphone is not null)
        {
            var samples = microphone.Drain();
            if (!samples.IsEmpty)
            {
                mixer.SetGain(microphoneSource, MicVolume);
                mixer.Push(microphoneSource, samples);
                MicLevelMeter.PublishIfActive(samples);
            }
        }

        // The batch is handed to the consumer, so it must be a fresh array (Mix returns one).
        var batch = mixer.Mix();

        if (TraceEnabled && ++_traceDrains % 20 == 0)
            Console.Error.WriteLine(
                $"[capture] loopback devices={loopbacks.Count} loudest peak={loopbackPeak:F3} " +
                $"mic={(microphone is null ? "off" : "on")} out={batch.Length} out peak={Peak(batch):F3}");

        if (batch.Length == 0)
            return;

        buffer.Enqueue(batch);
        AudioLevelMeter.Publish(batch);
        signal.Set();
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
                peak = magnitude;
        }

        return peak;
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

        public static CaptureHandle CreateLoopback(string deviceId, CaptureState state, int targetRate)
        {
            // WasapiLoopbackCapture is the proven loopback path (kept from the previous provider):
            // the WasapiRecorder builder API in the NAudio 3 preview never raised DataAvailable
            // in this scenario, so loopback stayed silent (see capture-diag.log investigation).
            // Loopback streams do not signal the WASAPI event, so this one stays in polling mode.
            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            try
            {
                device = enumerator.GetDevice(deviceId);
                var capture = new WasapiLoopbackCapture(device);
                return new CaptureHandle(capture, device, enumerator, state, targetRate);
            }
            catch
            {
                device?.Dispose();
                enumerator.Dispose();
                throw;
            }
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
