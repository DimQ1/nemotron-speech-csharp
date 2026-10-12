using System.Threading.Channels;

namespace SpeechLib.AudioStorage;

/// <summary>
/// Non-blocking decorator: <see cref="Append"/> copies the samples into an unbounded
/// channel and returns; a background task encodes and writes them. Use it from the
/// recognition loop so a slow disk or encoder never delays recognition.
/// <para>
/// <see cref="SamplesWritten"/> and <see cref="Gaps"/> are updated synchronously on the
/// caller's thread, so segment timestamps can be taken right after <see cref="Append"/>.
/// A background write failure stops further writing (recognition continues) and is
/// rethrown by <see cref="CompleteAsync"/>; it is also reported through <see cref="Faulted"/>.
/// </para>
/// Thread-safety: one producer thread calls Append/AddGap/CompleteAsync.
/// </summary>
public sealed class QueuedRecordingAudioWriter : IRecordingAudioWriter
{
    private readonly IRecordingAudioWriter _inner;
    private readonly RecordingTimeline _timeline;
    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly Task _pump;
    private volatile Exception? _error;
    private bool _completed;

    public QueuedRecordingAudioWriter(IRecordingAudioWriter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeline = new RecordingTimeline(inner.SampleRate);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Raised once, on the background task, when writing fails.</summary>
    public event Action<Exception>? Faulted;

    public RecordingAudioFormat Format => _inner.Format;
    public int SampleRate => _inner.SampleRate;
    public string FinalPath => _inner.FinalPath;
    public string PartPath => _inner.PartPath;
    public long SamplesWritten => _timeline.SamplesWritten;
    public IReadOnlyList<RecordingGap> Gaps => _timeline.Gaps;

    /// <summary>The background write error, if any.</summary>
    public Exception? Error => _error;

    public void Append(ReadOnlySpan<float> samples)
    {
        if (_completed) throw new InvalidOperationException("The recording is already completed.");
        if (samples.IsEmpty) return;
        _timeline.Advance(samples.Length);
        if (_error is null)
            _channel.Writer.TryWrite(new Item(samples.ToArray(), 0));
    }

    public void AddGap(long mutedSamples)
    {
        if (_completed) throw new InvalidOperationException("The recording is already completed.");
        if (mutedSamples <= 0) return;
        _timeline.AddGap(mutedSamples);
        if (_error is null)
            _channel.Writer.TryWrite(new Item(null, mutedSamples));
    }

    public async Task<RecordingAudioResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) throw new InvalidOperationException("The recording is already completed.");
        _completed = true;
        _channel.Writer.TryComplete();
        await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_error is not null)
            throw new IOException($"Recording audio could not be written: {_error.Message}", _error);
        return await _inner.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops without finalizing: drains the queue and leaves the <c>.part</c> file for recovery.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _pump.ConfigureAwait(false); }
        catch { /* already captured in _error */ }
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_error is not null) continue;
                if (item.Samples is not null)
                    _inner.Append(item.Samples);
                else
                    _inner.AddGap(item.GapSamples);
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            Faulted?.Invoke(ex);
            // Drain so producers never pile up memory after a failure.
            while (_channel.Reader.TryRead(out _)) { }
        }
    }

    private readonly record struct Item(float[]? Samples, long GapSamples);
}
