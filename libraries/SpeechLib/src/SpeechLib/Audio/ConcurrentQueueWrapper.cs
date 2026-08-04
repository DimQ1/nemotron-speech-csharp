using System.Collections.Concurrent;

namespace SpeechLib.Audio;

/// <summary>
/// Thin wrapper around ConcurrentQueue that accepts float[] batches.
/// Avoids per-sample atomic operations (was ~16000/sec, now ~10/sec).
/// </summary>
public sealed class ConcurrentQueueWrapper
{
    private readonly ConcurrentQueue<float[]> _queue = new();
    private readonly int _capacity;
    private int _count;
    private long _droppedBatches;

    public ConcurrentQueueWrapper(int capacity = 64)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    /// <summary>Maximum number of batches retained while the consumer catches up.</summary>
    public int Capacity => _capacity;

    /// <summary>Current approximate number of queued batches.</summary>
    public int Count => Math.Max(0, Volatile.Read(ref _count));

    /// <summary>Number of batches discarded because the queue was full.</summary>
    public long DroppedBatches => Volatile.Read(ref _droppedBatches);

    /// <summary>
    /// Enqueue a batch without blocking the capture callback. When full, the oldest
    /// batches are discarded to keep memory bounded and preserve recent speech.
    /// </summary>
    public bool Enqueue(float[] batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Length == 0)
            return false;

        _queue.Enqueue(batch);
        Interlocked.Increment(ref _count);

        while (Volatile.Read(ref _count) > _capacity && TryDequeueCore(out _))
            Interlocked.Increment(ref _droppedBatches);

        return true;
    }

    public bool TryDequeue(out float[] batch) => TryDequeueCore(out batch);

    public bool IsEmpty => _queue.IsEmpty;

    private bool TryDequeueCore(out float[] batch)
    {
        if (!_queue.TryDequeue(out batch!))
            return false;

        Interlocked.Decrement(ref _count);
        return true;
    }
}
