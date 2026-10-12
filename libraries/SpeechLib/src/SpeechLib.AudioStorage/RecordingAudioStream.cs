using System.Buffers.Binary;
using Concentus;
using Concentus.Structs;
using SpeechLib.AudioStorage.Ogg;

namespace SpeechLib.AudioStorage;

/// <summary>
/// Seekable, streaming mono float reader of a stored recording (Ogg Opus or WAV) for playback.
/// <para>
/// Unlike <see cref="RecordingAudioReader.ReadMono(string,int)"/> it never holds the whole file in
/// memory: a 2 h recording at 16 kHz would be ~460 MB as float. Memory use is one Ogg page of
/// decoded audio (~1 s) plus a page index (16 bytes per page, ~115 KB for 2 h).
/// </para>
/// <para>
/// Ogg Opus seeking uses a page index (byte offset + granule) built at <see cref="Open"/> and
/// decodes from the page that starts at least 80 ms (RFC 7845 §4.6 pre-roll) before the target,
/// so the decoder has converged by the target sample. Sample positions are exact: they follow
/// the page granules, with the same pre-skip/end trimming as <see cref="RecordingAudioReader"/>.
/// </para>
/// Not thread-safe; callers serialize access.
/// </summary>
public sealed class RecordingAudioStream : IDisposable
{
    private readonly Source _source;

    private RecordingAudioStream(Source source) => _source = source;

    /// <summary>Samples per second of the decoded audio.</summary>
    public int SampleRate => _source.SampleRate;

    /// <summary>Total length in samples.</summary>
    public long Length => _source.Length;

    /// <summary>Total length.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Length / SampleRate);

    /// <summary>Index of the next sample <see cref="Read"/> returns.</summary>
    public long Position => _source.Position;

    /// <summary>
    /// Opens <paramref name="path"/>. Ogg Opus decodes at <paramref name="opusSampleRate"/>
    /// (8/12/16/24/48 kHz); a WAV plays at its own rate. Finished files and <c>.part</c>
    /// files alike (stops at the first damaged page). Scans the Ogg page headers once.
    /// </summary>
    public static RecordingAudioStream Open(string path, int opusSampleRate = RecordingAudioReader.RecognizerSampleRate)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        try
        {
            return Open(stream, opusSampleRate);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="Open(string,int)"/>
    /// <remarks>Takes ownership of <paramref name="stream"/> (must be seekable).</remarks>
    public static RecordingAudioStream Open(Stream stream, int opusSampleRate = RecordingAudioReader.RecognizerSampleRate)
    {
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
        Span<byte> magic = stackalloc byte[4];
        long start = stream.Position;
        if (stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4)
            throw new InvalidDataException("Audio file is empty.");
        stream.Position = start;
        if (magic.SequenceEqual("OggS"u8)) return new RecordingAudioStream(OggSource.Create(stream, opusSampleRate));
        if (magic.SequenceEqual("RIFF"u8)) return new RecordingAudioStream(WavSource.Create(stream));
        throw new InvalidDataException("Unsupported audio file (expected Ogg Opus or WAV).");
    }

    /// <summary>Moves to <paramref name="sample"/> (clamped to [0, <see cref="Length"/>]).</summary>
    public void Seek(long sample) => _source.Seek(Math.Clamp(sample, 0, Length));

    /// <summary>Fills <paramref name="buffer"/> with mono samples; returns the count (0 at the end).</summary>
    public int Read(Span<float> buffer) => _source.Read(buffer);

    public void Dispose() => _source.Dispose();

    private abstract class Source : IDisposable
    {
        public int SampleRate { get; protected init; }
        public long Length { get; protected init; }
        public long Position { get; protected set; }
        public abstract void Seek(long sample);
        public abstract int Read(Span<float> buffer);
        public abstract void Dispose();
    }

    private sealed class WavSource : Source
    {
        private readonly Stream _stream;
        private readonly long _dataOffset;
        private readonly int _channels;
        private readonly bool _isFloat;
        private readonly int _frameBytes;
        private byte[] _bytes = [];

        private WavSource(Stream stream, long dataOffset, long frames, int channels, int rate, bool isFloat)
        {
            _stream = stream;
            _dataOffset = dataOffset;
            _channels = channels;
            _isFloat = isFloat;
            _frameBytes = channels * (isFloat ? 4 : 2);
            SampleRate = rate;
            Length = frames;
        }

        public static WavSource Create(Stream stream)
        {
            using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            br.ReadBytes(12);
            int channels = 0, rate = 0, bits = 0, formatTag = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));
                uint size = br.ReadUInt32();
                if (id == "fmt ")
                {
                    var fmt = br.ReadBytes((int)size);
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                    rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                    if (formatTag == 0xFFFE && fmt.Length >= 26)
                        formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                    if ((size & 1) != 0) stream.Seek(1, SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    if (channels <= 0 || rate <= 0) throw new InvalidDataException("WAV data before fmt chunk.");
                    bool isFloat = formatTag == 3 && bits == 32;
                    if (!isFloat && !(formatTag == 1 && bits == 16))
                        throw new InvalidDataException($"Unsupported WAV encoding (tag {formatTag}, {bits}-bit).");
                    // A .part or >4 GB file may carry a stale/clamped size: read to end of file instead.
                    long available = stream.Length - stream.Position;
                    long dataBytes = size == 0 || size == uint.MaxValue || size > available ? available : size;
                    long frames = dataBytes / (channels * (bits / 8));
                    return new WavSource(stream, stream.Position, frames, channels, rate, isFloat);
                }
                else
                {
                    stream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
            throw new InvalidDataException("WAV has no data chunk.");
        }

        public override void Seek(long sample) => Position = sample;

        public override int Read(Span<float> buffer)
        {
            int frames = (int)Math.Min(buffer.Length, Length - Position);
            if (frames <= 0) return 0;
            int bytes = frames * _frameBytes;
            if (_bytes.Length < bytes) _bytes = new byte[bytes];
            _stream.Position = _dataOffset + Position * _frameBytes;
            int got = _stream.ReadAtLeast(_bytes.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
            frames = got / _frameBytes;
            var src = _bytes.AsSpan();
            for (int f = 0, o = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++, o += _isFloat ? 4 : 2)
                {
                    sum += _isFloat
                        ? BinaryPrimitives.ReadSingleLittleEndian(src[o..])
                        : BinaryPrimitives.ReadInt16LittleEndian(src[o..]) / 32768f;
                }
                buffer[f] = sum / _channels;
            }
            Position += frames;
            return frames;
        }

        public override void Dispose() => _stream.Dispose();
    }

    private sealed class OggSource : Source
    {
        /// <summary>Decoder pre-roll before a seek target, in 48 kHz granule units (80 ms).</summary>
        private const long PreRollGranules = OpusHeaders.GranuleRate * 80 / 1000;

        private readonly Stream _stream;
        private readonly OggPageReader _reader;
        private readonly IOpusDecoder _decoder;
        private readonly int _channels;
        private readonly long _preSkip;         // output samples
        private readonly long[] _offsets;       // audio page byte offsets
        private readonly long[] _granules;      // audio page granules (-1: no packet ends on it)
        private readonly float[] _pcm;          // one packet, interleaved
        private readonly List<byte> _pending = new();
        private float[] _block = new float[4096]; // decoded audio of the current page (mono)
        private long _blockStart;               // sample index of _block[0]
        private int _blockCount;
        private int _nextPage;                  // index into _offsets of the page to decode next
        private long _lastPageEnd = long.MinValue; // end sample of the previous decoded page (sequential decode)
        private bool _skipContinuation;         // first packet on _nextPage started on a page we did not read

        private OggSource(Stream stream, int sampleRate, int channels, long preSkip, long[] offsets, long[] granules, long length)
        {
            _stream = stream;
            _reader = new OggPageReader(stream);
            _channels = channels;
            _preSkip = preSkip;
            _offsets = offsets;
            _granules = granules;
            SampleRate = sampleRate;
            Length = length;
#pragma warning disable CS0618 // managed decoder on purpose (no native probing)
            _decoder = new OpusDecoder(sampleRate, channels);
#pragma warning restore CS0618
            _pcm = new float[sampleRate * 120 / 1000 * channels];
        }

        public static OggSource Create(Stream stream, int sampleRate)
        {
            if (!OggOpusRecordingWriter.SupportedSampleRates.Contains(sampleRate))
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus decodes to 8, 12, 16, 24 or 48 kHz.");

            var reader = new OggPageReader(stream);
            var offsets = new List<long>();
            var granules = new List<long>();
            int channels = 0, preSkip48k = 0, headerPackets = 0;
            long lastGranule = -1;
            while (reader.TryReadPage(out var page))
            {
                if (headerPackets < 2)
                {
                    // Header pages: OpusHead alone on the first page, OpusTags on the next (may span pages).
                    foreach (var (segment, complete) in page.Packets())
                    {
                        if (!complete) continue;
                        if (headerPackets == 0 && !OpusHeaders.TryParseHead(segment, out channels, out preSkip48k, out _))
                            throw new InvalidDataException("Ogg stream has no OpusHead.");
                        headerPackets++;
                    }
                    if (headerPackets == 0) throw new InvalidDataException("Ogg stream has no OpusHead.");
                    if (channels > 2) throw new InvalidDataException("Not a mono/stereo Ogg Opus stream.");
                    continue;
                }

                offsets.Add(page.Offset);
                granules.Add(page.Granule);
                if (page.Granule >= 0) lastGranule = page.Granule;
                if ((page.Flags & OggPageFlags.EndOfStream) != 0) break;
            }
            if (headerPackets == 0) throw new InvalidDataException("Ogg stream has no OpusHead.");

            long preSkip = (long)preSkip48k * sampleRate / OpusHeaders.GranuleRate;
            long length = lastGranule < 0 ? 0 : Math.Max(0, lastGranule * sampleRate / OpusHeaders.GranuleRate - preSkip);
            var source = new OggSource(stream, sampleRate, channels, preSkip, offsets.ToArray(), granules.ToArray(), length);
            source.Seek(0);
            return source;
        }

        public override void Seek(long sample)
        {
            Position = sample;
            _blockCount = 0;
            _pending.Clear();
            _decoder.ResetState();
            _lastPageEnd = long.MinValue;

            // Last page whose audio starts at or before target − pre-roll. The audio of page p
            // starts where page p−1's granule ends (0 for the first audio page).
            long target48 = (sample + _preSkip) * OpusHeaders.GranuleRate / SampleRate - PreRollGranules;
            int page = 0;
            long previousEnd = 0;
            for (int p = 0; p < _granules.Length; p++)
            {
                if (previousEnd > target48) break;
                page = p;
                if (_granules[p] >= 0) previousEnd = _granules[p];
            }
            _nextPage = page;
            _skipContinuation = page > 0;
            if (page == 0) _lastPageEnd = -_preSkip;
        }

        public override int Read(Span<float> buffer)
        {
            int written = 0;
            while (written < buffer.Length && Position < Length)
            {
                long blockEnd = _blockStart + _blockCount;
                if (_blockCount == 0 || Position >= blockEnd)
                {
                    if (!DecodeNextPage()) break;
                    continue;
                }
                if (Position < _blockStart)
                {
                    // Audio lost to a corrupt packet: play silence so later samples keep their time.
                    int gap = (int)Math.Min(_blockStart - Position, buffer.Length - written);
                    buffer.Slice(written, gap).Clear();
                    written += gap;
                    Position += gap;
                    continue;
                }
                int from = (int)(Position - _blockStart);
                int n = (int)Math.Min(Math.Min(_blockCount - from, buffer.Length - written), Length - Position);
                _block.AsSpan(from, n).CopyTo(buffer[written..]);
                written += n;
                Position += n;
            }
            return written;
        }

        /// <summary>Decodes the next indexed page into <see cref="_block"/>; false at the end.</summary>
        private bool DecodeNextPage()
        {
            while (_nextPage < _offsets.Length)
            {
                int index = _nextPage++;
                _stream.Position = _offsets[index];
                if (!_reader.TryReadPage(out var page)) return false;

                bool continued = (page.Flags & OggPageFlags.Continued) != 0;
                bool first = true;
                int count = 0;
                foreach (var (segment, complete) in page.Packets())
                {
                    bool skip = first && continued && _skipContinuation;
                    first = false;
                    if (skip)
                    {
                        // Tail of a packet whose head is on a page we did not read.
                        if (complete) _skipContinuation = false;
                        continue;
                    }
                    _skipContinuation = false;
                    _pending.AddRange(segment);
                    if (!complete) break;
                    var packet = _pending.ToArray();
                    _pending.Clear();
                    int frames = _decoder.Decode(packet, _pcm, _pcm.Length / _channels, false);
                    if (frames <= 0) continue; // corrupt packet: skip; the page granule keeps timing right
                    EnsureBlock(count + frames);
                    for (int i = 0; i < frames; i++)
                        _block[count + i] = _channels == 1 ? _pcm[i] : (_pcm[2 * i] + _pcm[2 * i + 1]) * 0.5f;
                    count += frames;
                }
                _skipContinuation = false;

                if (page.Granule < 0 || count == 0)
                    continue; // no packet ends on this page

                long pageEnd = page.Granule * SampleRate / OpusHeaders.GranuleRate - _preSkip;
                bool last = (page.Flags & OggPageFlags.EndOfStream) != 0 || index == _offsets.Length - 1;
                // Normal pages are anchored at their end granule. The last page may be trimmed at
                // the end (RFC 7845 §4.4): anchor it at the previous page end instead.
                long start = last && _lastPageEnd != long.MinValue ? _lastPageEnd : pageEnd - count;
                _blockStart = start;
                _blockCount = (int)Math.Max(0, Math.Min(count, pageEnd - start));
                _lastPageEnd = pageEnd;
                if (_blockStart + _blockCount > Position) return true;
            }
            return false;
        }

        private void EnsureBlock(int size)
        {
            if (_block.Length < size) Array.Resize(ref _block, Math.Max(size, _block.Length * 2));
        }

        public override void Dispose() => _stream.Dispose();
    }
}
