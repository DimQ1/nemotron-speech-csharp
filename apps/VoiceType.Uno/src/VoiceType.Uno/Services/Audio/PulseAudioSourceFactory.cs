using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace VoiceType.Uno.Services.Audio;

/// <summary>
/// Linux capture provider backed by the PulseAudio compatibility server used by
/// WSLg and by common PipeWire desktop setups.
/// </summary>
public sealed class PulseAudioSourceFactory : IAudioSourceFactory
{
    public IAudioSource Create(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic => new PulseAudioSource(sampleRate, null),
        CaptureMode.Loopback => new PulseAudioSource(sampleRate, "@DEFAULT_MONITOR@"),
        CaptureMode.Mix => new PulseMixAudioSource(sampleRate),
        _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source.")
    };
}

internal sealed class PulseAudioSource : IAudioSource
{
    private readonly int _sampleRate;
    private readonly string? _device;

    public PulseAudioSource(int sampleRate, string? device)
    {
        _sampleRate = sampleRate;
        _device = device;
    }

    public int SourceSampleRate => _sampleRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        using var stream = new PulseCaptureStream(_sampleRate, _device);
        while (state.IsRunning)
        {
            var samples = stream.Read();
            if (samples.Length == 0)
                continue;

            buffer.Enqueue(samples);
            signal.Set();
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class PulseMixAudioSource : IAudioSource
{
    private readonly int _sampleRate;

    public PulseMixAudioSource(int sampleRate) => _sampleRate = sampleRate;

    public int SourceSampleRate => _sampleRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        using var microphone = new PulseCaptureStream(_sampleRate, null);
        using var loopback = new PulseCaptureStream(_sampleRate, "@DEFAULT_MONITOR@");

        var microphoneQueue = new ConcurrentQueue<float[]>();
        var loopbackQueue = new ConcurrentQueue<float[]>();
        Exception? captureException = null;

        var microphoneThread = StartReader(
            "VoiceType microphone capture",
            microphone,
            microphoneQueue,
            state,
            signal,
            ex => Interlocked.CompareExchange(ref captureException, ex, null));
        var loopbackThread = StartReader(
            "VoiceType loopback capture",
            loopback,
            loopbackQueue,
            state,
            signal,
            ex => Interlocked.CompareExchange(ref captureException, ex, null));

        try
        {
            while (state.IsRunning || microphoneThread.IsAlive || loopbackThread.IsAlive)
            {
                var microphoneSamples = Drain(microphoneQueue);
                var loopbackSamples = Drain(loopbackQueue);
                var mixed = Mix(microphoneSamples, loopbackSamples);
                if (mixed.Length > 0)
                {
                    buffer.Enqueue(mixed);
                    signal.Set();
                }
                else
                {
                    state.Wait(20);
                }
            }
        }
        finally
        {
            state.Stop();
            signal.Set();
            microphoneThread.Join(TimeSpan.FromSeconds(1));
            loopbackThread.Join(TimeSpan.FromSeconds(1));
        }

        if (captureException is not null)
            throw new InvalidOperationException("PulseAudio mixed capture failed.", captureException);
    }

    public void Dispose()
    {
    }

    private static Thread StartReader(
        string name,
        PulseCaptureStream stream,
        ConcurrentQueue<float[]> queue,
        CaptureState state,
        ManualResetEventSlim signal,
        Action<Exception> reportError)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (state.IsRunning)
                {
                    queue.Enqueue(stream.Read());
                    signal.Set();
                }
            }
            catch (Exception ex)
            {
                reportError(ex);
                state.Stop();
                signal.Set();
            }
        })
        {
            IsBackground = true,
            Name = name
        };
        thread.Start();
        return thread;
    }

    private static List<float> Drain(ConcurrentQueue<float[]> queue)
    {
        var samples = new List<float>();
        while (queue.TryDequeue(out var batch))
            samples.AddRange(batch);
        return samples;
    }

    private static float[] Mix(IReadOnlyList<float> microphone, IReadOnlyList<float> loopback)
    {
        var count = Math.Max(microphone.Count, loopback.Count);
        if (count == 0)
            return [];

        var mixed = new float[count];
        for (var i = 0; i < count; i++)
        {
            var microphoneSample = i < microphone.Count ? microphone[i] : 0f;
            var loopbackSample = i < loopback.Count ? loopback[i] : 0f;
            mixed[i] = Math.Clamp((microphoneSample + loopbackSample) * 0.5f, -1f, 1f);
        }

        return mixed;
    }
}

internal sealed class PulseCaptureStream : IDisposable
{
    private const int PaStreamRecord = 2;
    private const int PaSampleS16Le = 3;
    private const int FramesPerRead = 1024;

    private readonly byte[] _readBuffer = new byte[FramesPerRead * sizeof(short)];
    private nint _handle;

    public PulseCaptureStream(int sampleRate, string? device)
    {
        var sampleSpec = new PaSampleSpec
        {
            Format = PaSampleS16Le,
            Rate = checked((uint)sampleRate),
            Channels = 1
        };

        _handle = pa_simple_new(
            server: null,
            name: "VoiceType.Uno",
            direction: PaStreamRecord,
            device,
            streamName: "VoiceType capture",
            ref sampleSpec,
            channelMap: nint.Zero,
            bufferAttributes: nint.Zero,
            error: out var error);

        if (_handle == nint.Zero)
            throw new InvalidOperationException(
                $"PulseAudio capture stream could not be opened for '{device ?? "default source"}' (error {error}).");
    }

    public float[] Read()
    {
        var result = pa_simple_read(_handle, _readBuffer, (nuint)_readBuffer.Length, out var error);
        if (result < 0)
            throw new InvalidOperationException($"PulseAudio capture read failed (error {error}).");

        var samples = new float[FramesPerRead];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)(_readBuffer[i * 2] | (_readBuffer[i * 2 + 1] << 8));
            samples[i] = value / 32768f;
        }

        return samples;
    }

    public void Dispose()
    {
        if (_handle == nint.Zero)
            return;

        pa_simple_free(_handle);
        _handle = nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaSampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [DllImport("libpulse-simple.so.0", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern nint pa_simple_new(
        string? server,
        string name,
        int direction,
        string? device,
        string streamName,
        ref PaSampleSpec sampleSpec,
        nint channelMap,
        nint bufferAttributes,
        out int error);

    [DllImport("libpulse-simple.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pa_simple_read(nint stream, [Out] byte[] data, nuint bytes, out int error);

    [DllImport("libpulse-simple.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pa_simple_free(nint stream);
}
