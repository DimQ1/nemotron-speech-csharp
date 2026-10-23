using System.Buffers.Binary;
using System.Collections.Concurrent;
using VoiceType.Uno.Services.Audio;

namespace VoiceType.Uno.Tests.Fakes;

/// <summary>
/// A capture helper without a process: the test writes the bytes its stdout would carry and ends it like a helper that
/// exits. Closing stdin (<see cref="RequestStop"/>) ends the stream, as voicetype-audio does.
/// </summary>
internal sealed class FakePcmHelper : IPcmHelper
{
    private readonly BlockingStream _output = new();
    private volatile string _errors = "";

    public Stream Output => _output;
    public string ErrorText => _errors;
    public int? ExitCode { get; private set; }
    public bool StopRequested { get; private set; }
    public bool Disposed { get; private set; }

    public static byte[] Header(int rate)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, rate);
        return bytes;
    }

    public static byte[] RateChange(int rate)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, HelperPcmDecoder.RateChangeMarker);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), rate);
        return bytes;
    }

    public static byte[] Samples(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
        return bytes;
    }

    public static byte[] Constant(float value, int count) => Samples(Enumerable.Repeat(value, count).ToArray());

    public void Write(byte[] bytes) => _output.Add(bytes);

    /// <summary>The helper exits: its last stderr line explains why.</summary>
    public void Exit(int code, string stderr = "")
    {
        _errors = stderr;
        ExitCode = code;
        _output.Complete();
    }

    public void WaitForExit(TimeSpan timeout)
    {
    }

    public void RequestStop()
    {
        StopRequested = true;
        ExitCode ??= 0;
        _output.Complete();
    }

    public void Dispose()
    {
        Disposed = true;
        _output.Complete();
    }

    /// <summary>A read-only stream fed in blocks; Read blocks until a block or the end arrives.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly BlockingCollection<byte[]> _blocks = new();
        private byte[] _current = [];
        private int _offset;

        public void Add(byte[] block)
        {
            if (!_blocks.IsAddingCompleted)
                _blocks.Add(block);
        }

        public void Complete() => _blocks.CompleteAdding();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset == _current.Length)
            {
                if (!_blocks.TryTake(out var next, Timeout.Infinite))
                    return 0;
                _current = next;
                _offset = 0;
            }
            var n = Math.Min(count, _current.Length - _offset);
            Array.Copy(_current, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
