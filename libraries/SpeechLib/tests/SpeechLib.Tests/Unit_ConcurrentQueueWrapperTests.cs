using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_ConcurrentQueueWrapperTests
{
    [Fact]
    public void Enqueue_PreservesFifoOrder()
    {
        var queue = new ConcurrentQueueWrapper(capacity: 4);
        queue.Enqueue([1f]);
        queue.Enqueue([2f]);

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(1f, first[0]);
        Assert.Equal(2f, second[0]);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Enqueue_WhenFullDropsOldestAndCounts()
    {
        var queue = new ConcurrentQueueWrapper(capacity: 2);
        queue.Enqueue([1f]);
        queue.Enqueue([2f]);
        queue.Enqueue([3f]);

        Assert.Equal(1, queue.DroppedBatches);
        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out var oldest));
        Assert.Equal(2f, oldest[0]);
    }

    [Fact]
    public void Enqueue_IgnoresEmptyBatches()
    {
        var queue = new ConcurrentQueueWrapper();

        Assert.False(queue.Enqueue(Array.Empty<float>()));
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrentQueueWrapper(0));
    }
}
