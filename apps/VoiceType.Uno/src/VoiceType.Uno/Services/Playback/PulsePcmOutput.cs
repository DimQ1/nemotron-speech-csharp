using System.Runtime.InteropServices;

namespace VoiceType.Uno.Services.Playback;

/// <summary>
/// Linux output: PulseAudio simple API (<c>libpulse-simple.so.0</c>, also served by PipeWire
/// and WSLg), float32 mono, ~100 ms target buffer. A writer thread renders 20 ms chunks and
/// blocks in <c>pa_simple_write</c>; played frames = written − <c>pa_simple_get_latency</c>.
/// Same P/Invoke style as the capture side (<c>Services/Audio/PulseAudioSourceFactory.cs</c>).
/// </summary>
internal sealed class PulsePcmOutput : IPcmOutput
{
    private const int PaStreamPlayback = 1;
    private const int PaSampleFloat32Le = 5;
    private const int BufferMilliseconds = 100;
    private const int ChunkMilliseconds = 20;

    private readonly object _sync = new();
    private nint _handle;
    private Thread? _writer;
    private volatile bool _stopping;
    private long _writtenFrames;
    private int _sampleRate;

    public event Action<Exception>? Failed;

    public void Start(int sampleRate, PcmRender render)
    {
        Stop();
        var spec = new PaSampleSpec { Format = PaSampleFloat32Le, Rate = checked((uint)sampleRate), Channels = 1 };
        uint bytesPerSecond = (uint)sampleRate * sizeof(float);
        var attributes = new PaBufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = bytesPerSecond * BufferMilliseconds / 1000,
            PreBuf = uint.MaxValue,
            MinReq = uint.MaxValue,
            FragSize = uint.MaxValue,
        };
        var handle = pa_simple_new(null, "VoiceType.Uno", PaStreamPlayback, null, "VoiceType playback",
            ref spec, nint.Zero, ref attributes, out int error);
        if (handle == nint.Zero)
            throw new InvalidOperationException($"PulseAudio playback stream could not be opened (error {error}).");

        lock (_sync)
        {
            _handle = handle;
            _sampleRate = sampleRate;
            _writtenFrames = 0;
            _stopping = false;
        }
        _writer = new Thread(() => WriteLoop(handle, sampleRate, render))
        {
            IsBackground = true,
            Name = "VoiceType playback (PulseAudio)",
        };
        _writer.Start();
    }

    public void Stop()
    {
        var writer = _writer;
        if (writer is null) return;
        _stopping = true;
        _writer = null;
        // The current write returns within one chunk (≤ 20 ms of buffer space). If the server
        // hangs, leak the stream rather than free it under a thread that still uses it.
        bool joined = writer.Join(TimeSpan.FromSeconds(1));
        lock (_sync)
        {
            if (!joined)
            {
                _handle = nint.Zero;
                return;
            }
            if (_handle != nint.Zero)
            {
                pa_simple_flush(_handle, out _);
                pa_simple_free(_handle);
                _handle = nint.Zero;
            }
        }
    }

    public long PlayedFrames
    {
        get
        {
            lock (_sync)
            {
                if (_handle == nint.Zero) return 0;
                ulong latencyUs = pa_simple_get_latency(_handle, out _);
                long written = Interlocked.Read(ref _writtenFrames);
                if (latencyUs == ulong.MaxValue) return written;
                long latencyFrames = (long)(latencyUs * (ulong)_sampleRate / 1_000_000UL);
                return Math.Max(0, written - latencyFrames);
            }
        }
    }

    public void Dispose() => Stop();

    private void WriteLoop(nint handle, int sampleRate, PcmRender render)
    {
        var chunk = new float[sampleRate * ChunkMilliseconds / 1000];
        try
        {
            while (!_stopping)
            {
                render(chunk);
                var bytes = MemoryMarshal.AsBytes(chunk.AsSpan());
                if (pa_simple_write(handle, ref MemoryMarshal.GetReference(bytes), (nuint)bytes.Length, out int error) < 0)
                    throw new InvalidOperationException($"PulseAudio playback write failed (error {error}).");
                Interlocked.Add(ref _writtenFrames, chunk.Length);
            }
        }
        catch (Exception ex)
        {
            if (!_stopping) Failed?.Invoke(ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaSampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaBufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
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
        ref PaBufferAttr bufferAttributes,
        out int error);

    [DllImport("libpulse-simple.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pa_simple_write(nint stream, ref byte data, nuint bytes, out int error);

    [DllImport("libpulse-simple.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong pa_simple_get_latency(nint stream, out int error);

    [DllImport("libpulse-simple.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pa_simple_flush(nint stream, out int error);

    [DllImport("libpulse-simple.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void pa_simple_free(nint stream);
}
