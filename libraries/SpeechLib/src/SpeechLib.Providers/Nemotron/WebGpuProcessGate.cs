namespace SpeechLib.Nemotron;

/// <summary>
/// Lets at most one recognizer at a time use the WebGPU execution provider in this process; every other one runs on the CPU.
/// </summary>
/// <remarks>
/// <para>Measured: two recognizers with a WebGPU session each, running on two threads of one process, crashed or hung in three
/// of three runs (fail-fast 0xC0000409, heap corruption 0xC0000374, a hang); two processes sharing the GPU were fine, and so is
/// one recognizer used from different threads one after another. The plugin is not safe for concurrent use of its shared
/// device, and the apps do create a second recognizer next to a live one (the History offline job, the second engine of
/// <c>TranscribeWords</c>, benchmarks).</para>
/// <para>A holder that never disposes its recognizer does not block the process for good: the <see cref="Lease"/> has a
/// finalizer that gives the gate back.</para>
/// <para><see cref="ReportFailure"/> closes the gate for the rest of the process: a WebGPU session that failed to be created or
/// to run will not work better for the next recognizer, which would only pay the same seconds and warnings again.</para>
/// </remarks>
internal sealed class WebGpuProcessGate
{
    /// <summary>The gate of this process, shared by every recognizer.</summary>
    public static WebGpuProcessGate Shared { get; } = new();

    private readonly object _sync = new();
    private bool _held;
    private string? _failure;

    /// <summary>True while a <see cref="Lease"/> is out.</summary>
    public bool IsHeld
    {
        get { lock (_sync) return _held; }
    }

    /// <summary>Takes the gate, or says why it is closed.</summary>
    /// <param name="refusal">The reason when no lease was given.</param>
    /// <returns>The lease to dispose with the recognizer; null when the gate is held or closed.</returns>
    public Lease? TryAcquire(out string? refusal)
    {
        lock (_sync)
        {
            if (_failure is not null)
            {
                refusal = $"WebGPU failed earlier in this process ({_failure})";
                return null;
            }
            if (_held)
            {
                refusal = "another recognizer in this process already uses WebGPU (concurrent WebGPU recognizers crash)";
                return null;
            }

            _held = true;
            refusal = null;
            return new Lease(this);
        }
    }

    /// <summary>Closes the gate: later <see cref="TryAcquire"/> calls are refused with <paramref name="reason"/> (the first reason is kept).</summary>
    public void ReportFailure(string reason)
    {
        lock (_sync)
            _failure ??= reason;
    }

    private void Release()
    {
        lock (_sync)
            _held = false;
    }

    /// <summary>Proof that the holder is the one WebGPU recognizer of the process; disposing it (or collecting it) hands the gate back.</summary>
    public sealed class Lease : IDisposable
    {
        private WebGpuProcessGate? _gate;

        internal Lease(WebGpuProcessGate gate) => _gate = gate;

        /// <summary>Gives the gate back; repeated calls do nothing.</summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            GC.SuppressFinalize(this);
        }

        ~Lease() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
