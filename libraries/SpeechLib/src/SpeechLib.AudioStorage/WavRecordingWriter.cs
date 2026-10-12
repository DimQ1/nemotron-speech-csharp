using System.Buffers.Binary;

namespace SpeechLib.AudioStorage;

/// <summary>
/// WAV PCM16 mono writer ("High" quality). The RIFF/data sizes are patched on every
/// flush (~1 s of audio), so even a <c>.part</c> file is a playable WAV up to its last
/// flush; <see cref="RecordingAudioRecovery"/> re-patches them from the file length.
/// </summary>
public sealed class WavRecordingWriter : IRecordingAudioWriter
{
    internal const int HeaderSize = 44;

    private readonly FileStream _file;
    private readonly RecordingTimeline _timeline;
    private readonly long _samplesPerFlush;
    private readonly byte[] _buffer = new byte[8192];
    private long _lastFlushAt;
    private bool _completed;
    private bool _disposed;

    public WavRecordingWriter(string finalPath, int sampleRate = 16000, double flushIntervalSeconds = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        FinalPath = Path.GetFullPath(finalPath);
        PartPath = FinalPath + RecordingAudioRecovery.PartSuffix;
        _timeline = new RecordingTimeline(sampleRate);
        _samplesPerFlush = Math.Max(1, (long)(flushIntervalSeconds * sampleRate));

        Directory.CreateDirectory(Path.GetDirectoryName(FinalPath)!);
        _file = new FileStream(PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        _file.Write(BuildHeader(sampleRate, 0));
        _file.Flush(flushToDisk: true);
    }

    public RecordingAudioFormat Format => RecordingAudioFormat.High;
    public int SampleRate { get; }
    public string FinalPath { get; }
    public string PartPath { get; }
    public long SamplesWritten => _timeline.SamplesWritten;
    public IReadOnlyList<RecordingGap> Gaps => _timeline.Gaps;

    public void Append(ReadOnlySpan<float> samples)
    {
        ThrowIfClosed();
        _timeline.Advance(samples.Length);
        while (!samples.IsEmpty)
        {
            int n = Math.Min(samples.Length, _buffer.Length / 2);
            for (int i = 0; i < n; i++)
            {
                float s = Math.Clamp(samples[i], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(i * 2), (short)Math.Round(s * 32767f));
            }
            _file.Write(_buffer, 0, n * 2);
            samples = samples[n..];
        }

        if (_timeline.SamplesWritten - _lastFlushAt >= _samplesPerFlush)
            FlushWithHeader();
    }

    public void AddGap(long mutedSamples)
    {
        ThrowIfClosed();
        _timeline.AddGap(mutedSamples);
    }

    public Task<RecordingAudioResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        cancellationToken.ThrowIfCancellationRequested();
        PatchSizes(_file, _timeline.SamplesWritten * 2);
        _file.Flush(flushToDisk: true);
        long bytes = _file.Length;
        _file.Dispose();
        _completed = true;
        File.Move(PartPath, FinalPath, overwrite: true);
        return Task.FromResult(new RecordingAudioResult(
            FinalPath, Format, SampleRate, _timeline.SamplesWritten, bytes, _timeline.Gaps.ToArray()));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (!_completed)
        {
            try { FlushWithHeader(); }
            catch (IOException) { }
            _file.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private void FlushWithHeader()
    {
        PatchSizes(_file, _timeline.SamplesWritten * 2);
        _file.Flush(flushToDisk: false);
        _lastFlushAt = _timeline.SamplesWritten;
    }

    internal static byte[] BuildHeader(int sampleRate, long dataBytes)
    {
        var h = new byte[HeaderSize];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), ClampSize(36 + dataBytes));
        "WAVE"u8.CopyTo(h.AsSpan(8));
        "fmt "u8.CopyTo(h.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), ClampSize(dataBytes));
        return h;
    }

    /// <summary>Rewrites the RIFF and data chunk sizes in place, keeping the stream position.</summary>
    internal static void PatchSizes(FileStream file, long dataBytes)
    {
        long pos = file.Position;
        Span<byte> size = stackalloc byte[4];
        file.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(size, ClampSize(36 + dataBytes));
        file.Write(size);
        file.Position = 40;
        BinaryPrimitives.WriteUInt32LittleEndian(size, ClampSize(dataBytes));
        file.Write(size);
        file.Position = pos;
    }

    // RIFF sizes are 32-bit; past 4 GB (~37 h at 16 kHz) players read to end of file.
    private static uint ClampSize(long value) => (uint)Math.Min(value, uint.MaxValue);

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The recording is already completed.");
    }
}
