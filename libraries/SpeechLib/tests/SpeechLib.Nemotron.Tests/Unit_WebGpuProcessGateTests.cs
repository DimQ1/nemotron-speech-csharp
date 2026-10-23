using System.Runtime.CompilerServices;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>At most one WebGPU recognizer per process: the gate, its lease and its failure latch.</summary>
public sealed class Unit_WebGpuProcessGateTests
{
    [Fact]
    public void TryAcquire_ShouldGiveOneLeaseAtATime()
    {
        var gate = new WebGpuProcessGate();

        var first = gate.TryAcquire(out var refusalOfFirst);
        var second = gate.TryAcquire(out var refusalOfSecond);

        Assert.NotNull(first);
        Assert.Null(refusalOfFirst);
        Assert.Null(second);
        Assert.Contains("another recognizer in this process already uses WebGPU", refusalOfSecond);
        Assert.True(gate.IsHeld);
        GC.KeepAlive(first);
    }

    [Fact]
    public void Dispose_ShouldGiveTheGateBack_AndDisposingTwiceShouldNotTakeAnotherHoldersLease()
    {
        var gate = new WebGpuProcessGate();
        var first = gate.TryAcquire(out _)!;

        first.Dispose();
        var second = gate.TryAcquire(out _);
        first.Dispose();   // the first holder disposes again: the second holder keeps the gate

        Assert.NotNull(second);
        Assert.True(gate.IsHeld);
        Assert.Null(gate.TryAcquire(out _));
        GC.KeepAlive(second);
    }

    [Fact]
    public void Lease_NeverDisposed_ShouldGiveTheGateBackWhenItIsCollected()
    {
        var gate = new WebGpuProcessGate();
        Abandon(gate);

        for (var attempt = 0; attempt < 20 && gate.IsHeld; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(gate.IsHeld);
        Assert.NotNull(gate.TryAcquire(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Abandon(WebGpuProcessGate gate) => Assert.NotNull(gate.TryAcquire(out _));

    [Fact]
    public void ReportFailure_ShouldRefuseEveryLaterCaller_WithTheFirstReason()
    {
        var gate = new WebGpuProcessGate();

        gate.ReportFailure("EP_FAIL");
        gate.ReportFailure("a later one");
        var lease = gate.TryAcquire(out var refusal);

        Assert.Null(lease);
        Assert.Equal("WebGPU failed earlier in this process (EP_FAIL)", refusal);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public void TryAcquire_ManyThreadsAtOnce_ShouldGiveExactlyOneLease()
    {
        var gate = new WebGpuProcessGate();
        var leases = new WebGpuProcessGate.Lease?[64];
        using var start = new ManualResetEventSlim();

        var threads = Enumerable.Range(0, leases.Length).Select(i => new Thread(() =>
        {
            start.Wait();
            leases[i] = gate.TryAcquire(out _);
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());

        Assert.Equal(1, leases.Count(l => l is not null));
        GC.KeepAlive(leases);
    }

    [Fact]
    public void Shared_ShouldBeOneGateForTheWholeProcess()
    {
        Assert.Same(WebGpuProcessGate.Shared, WebGpuProcessGate.Shared);
    }
}
