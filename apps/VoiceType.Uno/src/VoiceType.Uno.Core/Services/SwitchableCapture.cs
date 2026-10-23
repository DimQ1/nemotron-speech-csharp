using System.Collections.Concurrent;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace VoiceType.Uno.Services;

/// <summary>What <see cref="SwitchableCapture.SwitchToAsync"/> did.</summary>
public enum CaptureSwitchOutcome
{
    /// <summary>Capture now runs on the requested source.</summary>
    Switched,

    /// <summary>Nothing to do: the requested source is the one in use.</summary>
    Unchanged,

    /// <summary>No capture is running (not started, or already stopped): the source is picked when it starts.</summary>
    NotRunning,

    /// <summary>The requested source could not be started; capture carries on with the previous one.</summary>
    Refused,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Active">The source capture runs on after the call.</param>
/// <param name="Error">Why the source was refused (<see cref="CaptureSwitchOutcome.Refused"/> only).</param>
public readonly record struct CaptureSwitchResult(CaptureSwitchOutcome Outcome, CaptureMode Active, Exception? Error = null);

/// <summary>
/// The capture side of a recording session: one <see cref="IAudioSource"/> on its own thread pushing batches into a
/// shared queue, which can be <b>replaced while it runs</b> (microphone, system audio, both) without ending the
/// session. The consumer keeps reading the same queue and never notices anything but a short gap and a marker.
/// </summary>
/// <remarks>
/// <para>A switch stops the current source, puts a marker batch into the queue (<see cref="IsSwitchMarker"/>: the
/// consumer learns where in the stream the source changed, in order with the audio), then starts the new source. A source
/// that fails to start within <see cref="StartGrace"/> is dropped and the previous source is started again, so a
/// bad choice (no output device for system audio, a busy microphone) costs a moment of audio, never the recording.</para>
/// <para><see cref="IsActive"/> stays true for the whole switch, so a consumer that stops reading when capture ends
/// does not end the session in the gap between the two sources.</para>
/// </remarks>
public sealed class SwitchableCapture : IDisposable
{
    private static readonly TimeSpan DefaultStartGrace = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly IAudioSourceFactory _factory;
    private readonly int _sampleRate;
    private readonly ConcurrentQueueWrapper _buffer;
    private readonly ManualResetEventSlim _signal;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly ConcurrentDictionary<float[], CaptureMode> _markers = new(ReferenceEqualityComparer.Instance);

    private Active? _active;
    private int _switching;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <param name="factory">Creates the sources.</param>
    /// <param name="sampleRate">The rate every source delivers (the recognizer's).</param>
    /// <param name="buffer">The queue the sources fill and the consumer drains.</param>
    /// <param name="signal">Set whenever there is something new for the consumer (data, a marker, the end).</param>
    public SwitchableCapture(IAudioSourceFactory factory, int sampleRate, ConcurrentQueueWrapper buffer, ManualResetEventSlim signal)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _sampleRate = sampleRate;
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    /// <summary>
    /// How long a new source may take to fail before it counts as started. Device errors (no output device, microphone in
    /// use) surface at the start of <see cref="IAudioSource.Start"/>, within a few hundred milliseconds.
    /// </summary>
    public TimeSpan StartGrace { get; init; } = DefaultStartGrace;

    /// <summary>The source capture runs on (the last one started).</summary>
    public CaptureMode Mode => Volatile.Read(ref _active)?.Mode ?? default;

    /// <summary>True while a source is running or being exchanged; false once capture has ended for good.</summary>
    public bool IsActive
    {
        get
        {
            if (Volatile.Read(ref _switching) != 0)
                return true;
            var active = Volatile.Read(ref _active);
            return active is not null && (active.State.IsRunning || active.Thread.IsAlive);
        }
    }

    /// <summary>
    /// The current source stopped with an error (not a source that a switch replaced). Raised on the capture thread.
    /// The session cannot go on: the consumer ends it.
    /// </summary>
    public event Action<Exception>? Failed;

    /// <summary>Starts capture on <paramref name="mode"/>. A source that cannot even be created throws; a start error is <see cref="Failed"/>.</summary>
    public void Start(CaptureMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _active) is not null)
            throw new InvalidOperationException("Capture was already started.");

        var source = _factory.Create(mode, _sampleRate);
        Volatile.Write(ref _active, Launch(source, mode, probe: null));
    }

    /// <summary>
    /// Asks the current source to stop and returns at once; <see cref="IsActive"/> turns false when its thread has ended.
    /// A switch that is under way ends with capture stopped.
    /// </summary>
    public void Stop()
    {
        _stopRequested = true;
        Volatile.Read(ref _active)?.State.Stop();
        _signal.Set();
    }

    /// <summary>
    /// Replaces the source while capture runs. Concurrent calls are served one after the other. See the class remarks for
    /// what the consumer sees and for the fallback when the new source does not start.
    /// </summary>
    public async Task<CaptureSwitchResult> SwitchToAsync(CaptureMode mode, CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SwitchCore(mode), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    /// <summary>Whether <paramref name="batch"/> is the marker of a source switch rather than audio; <paramref name="mode"/> is the new source.</summary>
    public bool IsSwitchMarker(float[] batch, out CaptureMode mode) => _markers.TryRemove(batch, out mode);

    /// <summary>Waits for the capture thread to end (after <see cref="Stop"/>).</summary>
    public bool Join(TimeSpan timeout) => Volatile.Read(ref _active)?.Thread.Join(timeout) ?? true;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Stop();
        var active = Interlocked.Exchange(ref _active, null);
        if (active is not null)
        {
            active.Thread.Join(TimeSpan.FromSeconds(1));
            active.Source.Dispose();
            active.State.Dispose();
        }
    }

    // ── the switch ──────────────────────────────────────────────────────────

    private CaptureSwitchResult SwitchCore(CaptureMode mode)
    {
        var current = Volatile.Read(ref _active);
        if (_stopRequested || _disposed || current is null)
            return new CaptureSwitchResult(CaptureSwitchOutcome.NotRunning, current?.Mode ?? default);
        if (current.Mode == mode)
            return new CaptureSwitchResult(CaptureSwitchOutcome.Unchanged, mode);

        // Nothing is touched until the new source exists: a source that cannot be created leaves capture as it was.
        IAudioSource next;
        try
        {
            next = _factory.Create(mode, _sampleRate);
        }
        catch (Exception ex)
        {
            return new CaptureSwitchResult(CaptureSwitchOutcome.Refused, current.Mode, ex);
        }

        Volatile.Write(ref _switching, 1);
        try
        {
            Retire(current);
            if (_stopRequested)
            {
                next.Dispose();
                return new CaptureSwitchResult(CaptureSwitchOutcome.NotRunning, current.Mode);
            }

            PostMarker(mode);
            var probe = new StartProbe();
            var started = Launch(next, mode, probe);
            var ended = started.Thread.Join(StartGrace);
            // From here on a failure of the new source is a normal one (Failed); one that came in during the very
            // last moments of the grace period is returned here.
            var failure = probe.EndGrace();
            if (ended || failure is not null)
            {
                // The source never captured. Go back to the previous one.
                started.Retired = true;
                started.State.Stop();
                if (started.Thread.Join(StopTimeout))
                    started.State.Dispose();
                next.Dispose();
                return Fallback(current.Mode, failure ?? new InvalidOperationException("The audio source stopped right after it started."));
            }

            Volatile.Write(ref _active, started);
            // Stop() may have run while the old state was still the published one: it then missed the new source.
            if (_stopRequested)
                started.State.Stop();
            return new CaptureSwitchResult(CaptureSwitchOutcome.Switched, mode);
        }
        finally
        {
            Volatile.Write(ref _switching, 0);
            _signal.Set();
        }
    }

    /// <summary>The new source did not start: capture goes back to the source it had, or the session ends with that failure.</summary>
    private CaptureSwitchResult Fallback(CaptureMode previous, Exception refusedBecause)
    {
        if (_stopRequested)
            return new CaptureSwitchResult(CaptureSwitchOutcome.Refused, previous, refusedBecause);

        PostMarker(previous);
        try
        {
            var source = _factory.Create(previous, _sampleRate);
            Volatile.Write(ref _active, Launch(source, previous, probe: null));
            if (_stopRequested)
                Volatile.Read(ref _active)?.State.Stop();
        }
        catch (Exception ex)
        {
            // Not even the old source comes back: the recording cannot continue.
            Failed?.Invoke(ex);
        }

        return new CaptureSwitchResult(CaptureSwitchOutcome.Refused, previous, refusedBecause);
    }

    /// <summary>Stops a source and waits for its thread. The source is only disposed once the thread is really gone.</summary>
    private static void Retire(Active active)
    {
        active.Retired = true;
        active.State.Stop();
        if (!active.Thread.Join(StopTimeout))
            return;
        active.Source.Dispose();
        active.State.Dispose();
    }

    private void PostMarker(CaptureMode mode)
    {
        var marker = new float[1];
        _markers[marker] = mode;
        _buffer.Enqueue(marker);
        _signal.Set();
    }

    private Active Launch(IAudioSource source, CaptureMode mode, StartProbe? probe)
    {
        var state = new CaptureState();
        Active active = null!;
        var thread = new Thread(() =>
        {
            try
            {
                source.Start(_buffer, _signal, state);
            }
            catch (Exception ex)
            {
                // A source that a switch is still trying out reports to the switch (which falls back); one that was
                // replaced ended on its own terms and nobody waits for it; any other failure ends the session.
                if (probe is not null && probe.TryRecordFailure(ex))
                {
                    // handled by SwitchCore
                }
                else if (!active.Retired)
                {
                    Failed?.Invoke(ex);
                }
            }
            finally
            {
                state.IsRunning = false;
                _signal.Set();
            }
        })
        {
            IsBackground = true,
            Name = "VoiceType audio capture",
        };
        active = new Active(source, state, thread, mode);
        thread.Start();
        return active;
    }

    private sealed class Active(IAudioSource source, CaptureState state, Thread thread, CaptureMode mode)
    {
        public IAudioSource Source { get; } = source;
        public CaptureState State { get; } = state;
        public Thread Thread { get; } = thread;
        public CaptureMode Mode { get; } = mode;

        /// <summary>Replaced by a switch (or given up): its end is not a failure of the session.</summary>
        public volatile bool Retired;
    }

    /// <summary>Carries a start error of a new source from its thread to <see cref="SwitchCore"/> while the grace period runs.</summary>
    private sealed class StartProbe
    {
        private readonly object _gate = new();
        private bool _inGrace = true;

        public Exception? Failure { get; private set; }

        /// <summary>Takes the error when the grace period is still running (false: the source had started, it is a normal failure).</summary>
        public bool TryRecordFailure(Exception ex)
        {
            lock (_gate)
            {
                if (!_inGrace)
                    return false;
                Failure = ex;
                return true;
            }
        }

        /// <summary>Ends the grace period; returns the error that was recorded during it, if any.</summary>
        public Exception? EndGrace()
        {
            lock (_gate)
            {
                _inGrace = false;
                return Failure;
            }
        }
    }
}
