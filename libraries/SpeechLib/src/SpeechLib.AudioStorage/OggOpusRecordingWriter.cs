using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using SpeechLib.AudioStorage.Ogg;

namespace SpeechLib.AudioStorage;

/// <summary>
/// Ogg Opus writer (mono, 20 ms frames, VBR). Encodes on the calling thread; wrap it in
/// <see cref="QueuedRecordingAudioWriter"/> to keep encoding off the recognition loop.
/// <para>
/// Crash safety: the OpusHead/OpusTags pages are flushed on open, then one audio page is
/// written and flushed per <c>flushInterval</c> of audio. A <c>.part</c> file therefore
/// always ends at (or just after) a complete page and is recovered by setting the
/// end-of-stream flag on its last valid page (<see cref="RecordingAudioRecovery"/>).
/// </para>
/// </summary>
public sealed class OggOpusRecordingWriter : IRecordingAudioWriter
{
    /// <summary>Sample rates Opus accepts natively (no resampling needed).</summary>
    public static readonly IReadOnlyList<int> SupportedSampleRates = [8000, 12000, 16000, 24000, 48000];

    private const int FrameMs = 20;
    private const int MaxPacketBytes = 1275;
    private const int MaxSegmentsPerPage = 255;

    private readonly FileStream _file;
    private readonly IOpusEncoder _encoder;
    private readonly RecordingTimeline _timeline;
    private readonly int _frameSize;
    private readonly int _granuleScale;
    private readonly int _lookahead;
    private readonly int _packetsPerFlush;
    private readonly float[] _frame;
    private readonly byte[] _packetBuffer = new byte[MaxPacketBytes];
    private readonly uint _serial;

    private readonly List<byte> _pageLacing = new(MaxSegmentsPerPage);
    private readonly MemoryStream _pageBody = new();
    private int _pagePackets;
    private uint _pageSequence;
    private int _frameFill;
    private long _encodedSamples;
    private bool _completed;
    private bool _disposed;

    public OggOpusRecordingWriter(string finalPath, RecordingAudioFormat format, int sampleRate = 16000, double flushIntervalSeconds = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(format);
        if (format.Codec != RecordingAudioCodec.OggOpus)
            throw new ArgumentException("Format must be Ogg Opus.", nameof(format));
        if (!SupportedSampleRates.Contains(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus supports 8, 12, 16, 24 and 48 kHz.");
        if (format.BitrateKbps is < 6 or > 128)
            throw new ArgumentOutOfRangeException(nameof(format), format.BitrateKbps, "Opus bitrate must be 6..128 kbps for mono.");

        Format = format;
        SampleRate = sampleRate;
        FinalPath = Path.GetFullPath(finalPath);
        PartPath = FinalPath + RecordingAudioRecovery.PartSuffix;
        _timeline = new RecordingTimeline(sampleRate);
        _frameSize = sampleRate * FrameMs / 1000;
        _frame = new float[_frameSize];
        _granuleScale = OpusHeaders.GranuleRate / sampleRate;
        _packetsPerFlush = Math.Max(1, (int)Math.Round(flushIntervalSeconds * 1000 / FrameMs));
        _serial = (uint)Random.Shared.Next();

        // Direct managed encoder: never probes for a native libopus, so behaviour and
        // output are identical on win-x64, linux-x64 and android-arm64.
#pragma warning disable CS0618 // OpusEncoder ctor is marked obsolete in favour of OpusCodecFactory (which may load native code)
        var encoder = new OpusEncoder(sampleRate, 1, format.Application == OpusApplicationMode.Voip
            ? OpusApplication.OPUS_APPLICATION_VOIP
            : OpusApplication.OPUS_APPLICATION_AUDIO);
#pragma warning restore CS0618
        encoder.Bitrate = format.BitrateKbps * 1000;
        encoder.UseVBR = true;
        encoder.UseConstrainedVBR = false;
        encoder.Complexity = Math.Clamp(format.Complexity, 0, 10);
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder = encoder;
        _lookahead = encoder.Lookahead;

        Directory.CreateDirectory(Path.GetDirectoryName(FinalPath)!);
        _file = new FileStream(PartPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 16 * 1024);
        WriteHeaderPages();
    }

    public RecordingAudioFormat Format { get; }
    public int SampleRate { get; }
    public string FinalPath { get; }
    public string PartPath { get; }
    public long SamplesWritten => _timeline.SamplesWritten;
    public IReadOnlyList<RecordingGap> Gaps => _timeline.Gaps;

    /// <summary>Encoder lookahead in input samples (= Opus pre-skip / granule scale).</summary>
    public int PreSkipSamples => _lookahead;

    public void Append(ReadOnlySpan<float> samples)
    {
        ThrowIfClosed();
        _timeline.Advance(samples.Length);
        while (!samples.IsEmpty)
        {
            int take = Math.Min(_frameSize - _frameFill, samples.Length);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill == _frameSize)
                EncodeFrame(final: false);
        }
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

        // Pad with silence until the encoder has emitted every real sample plus its
        // lookahead; the final granule then trims the padding back off (end trimming).
        long needed = _timeline.SamplesWritten + _lookahead;
        if (_frameFill > 0 || _encodedSamples < needed)
        {
            do
            {
                _frame.AsSpan(_frameFill).Clear();
                _frameFill = _frameSize;
                EncodeFrame(final: _encodedSamples + _frameSize >= needed);
            }
            while (_encodedSamples < needed);
        }
        else
        {
            // Every packet is already on disk; close the stream with an empty EOS page.
            WritePage(OggPageFlags.EndOfStream, FinalGranule());
        }

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
            // Abandoned: keep what is buffered so the .part stays as complete as possible.
            try
            {
                if (_pagePackets > 0)
                    WritePage(OggPageFlags.None, _encodedSamples * _granuleScale);
                _file.Flush(flushToDisk: true);
            }
            catch (IOException) { }
            _file.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private long FinalGranule() => (long)_lookahead * _granuleScale + _timeline.SamplesWritten * _granuleScale;

    private void EncodeFrame(bool final)
    {
        int bytes = _encoder.Encode(_frame, _frameSize, _packetBuffer, _packetBuffer.Length);
        if (bytes < 0)
            throw new InvalidOperationException($"Opus encode failed ({bytes}).");
        _frameFill = 0;
        _encodedSamples += _frameSize;

        int segments = bytes / 255 + 1;
        if (_pageLacing.Count + segments > MaxSegmentsPerPage)
            WritePage(OggPageFlags.None, (_encodedSamples - _frameSize) * _granuleScale);

        for (int remaining = bytes; ; remaining -= 255)
        {
            _pageLacing.Add((byte)Math.Min(remaining, 255));
            if (remaining < 255) break;
        }
        _pageBody.Write(_packetBuffer, 0, bytes);
        _pagePackets++;

        if (final)
            WritePage(OggPageFlags.EndOfStream, FinalGranule());
        else if (_pagePackets >= _packetsPerFlush)
            WritePage(OggPageFlags.None, _encodedSamples * _granuleScale);
    }

    private void WriteHeaderPages()
    {
        var head = OpusHeaders.BuildHead(1, _lookahead * _granuleScale, SampleRate);
        WriteSinglePacketPage(OggPageFlags.BeginOfStream, head);
        var tags = OpusHeaders.BuildTags(
            $"SpeechLib.AudioStorage ({_encoder.GetVersionString()})",
            ["ENCODER=SpeechLib.AudioStorage", $"ENCODER_OPTIONS={Format}"]);
        WriteSinglePacketPage(OggPageFlags.None, tags);
        _file.Flush(flushToDisk: true);
    }

    private void WriteSinglePacketPage(OggPageFlags flags, byte[] packet)
    {
        // Header packets are small (< 255 * 255 bytes), so one page each.
        var lacing = new List<byte>();
        for (int remaining = packet.Length; ; remaining -= 255)
        {
            lacing.Add((byte)Math.Min(remaining, 255));
            if (remaining < 255) break;
        }
        _file.Write(OggPage.Build(flags, 0, _serial, _pageSequence++, lacing.ToArray(), packet));
    }

    private void WritePage(OggPageFlags flags, long granule)
    {
        var page = OggPage.Build(flags, granule, _serial, _pageSequence++,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pageLacing),
            _pageBody.GetBuffer().AsSpan(0, (int)_pageBody.Length));
        _file.Write(page);
        // Process-crash safety: hand the page to the OS now. (A per-page fsync would cost
        // battery on phones; the final flushToDisk happens in CompleteAsync.)
        _file.Flush(flushToDisk: false);
        _pageLacing.Clear();
        _pageBody.SetLength(0);
        _pagePackets = 0;
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The recording is already completed.");
    }
}
