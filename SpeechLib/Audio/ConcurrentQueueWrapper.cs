using System.Collections.Concurrent;

namespace SpeechLib.Audio;

/// <summary>
/// Thin wrapper around ConcurrentQueue that accepts float[] batches.
/// Avoids per-sample atomic operations (was ~16000/sec, now ~10/sec).
/// </summary>
public sealed class ConcurrentQueueWrapper
{
    private readonly ConcurrentQueue<float[]> _queue = new();

    public void Enqueue(float[] batch) => _queue.Enqueue(batch);
    public bool TryDequeue(out float[] batch) => _queue.TryDequeue(out batch!);
    public bool IsEmpty => _queue.IsEmpty;
}
