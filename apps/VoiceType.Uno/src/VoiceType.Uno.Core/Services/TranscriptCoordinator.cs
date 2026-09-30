using SpeechLib.TextOutput;

namespace VoiceType.Uno.Services;

/// <summary>
/// Owns how recognized speech is merged into the transcript field: the debounce-free
/// throttle on partial results, the manual-input hand-off, and the "speech continues
/// after the text already in the field" rule.
/// </summary>
/// <remarks>
/// Thread affinity: every member must be called on the UI thread. Results arriving
/// from the decode thread (<see cref="OnPartialResult"/>, <see cref="OnFinalResult"/>)
/// hop onto the scheduler first. The transcript text itself lives in the ViewModel and
/// is read through <c>readTranscript</c>, because the field is user-editable in manual
/// input mode.
/// </remarks>
public sealed class TranscriptCoordinator : IDisposable
{
    /// <summary>Window applied to partial results (see <see cref="OnPartialResult"/>).</summary>
    public static readonly TimeSpan DefaultPartialThrottle = TimeSpan.FromMilliseconds(200);

    private readonly IUiScheduler _scheduler;
    private readonly Func<string> _readTranscript;
    private readonly SpeechAppendComposer _composer = new();
    private readonly IUiTimer _partialThrottle;

    private string _lastRecognizerText = "";
    private string _pendingPartial = "";
    private bool _hasPendingPartial;
    private bool _isManualInputEnabled;
    private bool _keepTextOnNextRecording;

    public TranscriptCoordinator(
        IUiScheduler scheduler,
        Func<string> readTranscript,
        TimeSpan? partialThrottle = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(readTranscript);

        _scheduler = scheduler;
        _readTranscript = readTranscript;
        _partialThrottle = scheduler.CreateOneShotTimer(
            partialThrottle ?? DefaultPartialThrottle,
            FlushPendingPartial);
    }

    /// <summary>The composed transcript changed and should be shown.</summary>
    public event Action<string>? TextChanged;

    /// <summary>A final result superseded the partials; commit and inject now.</summary>
    public event Action<string>? FinalCommitted;

    public bool IsManualInputEnabled => _isManualInputEnabled;

    /// <summary>
    /// Coalesces partial results so the transcript updates at most once per throttle
    /// window. This is a throttle, not a debounce: a recognizer that reports more often
    /// than the window (Nemotron reports every ~50 ms of audio) kept restarting a debounce
    /// window, so no text appeared until recording stopped.
    /// </summary>
    public void OnPartialResult(string recognizerText) =>
        _scheduler.Post(() => ApplyPartial(recognizerText));

    /// <summary>A committed result: supersedes queued partials and becomes transcript text.</summary>
    public void OnFinalResult(string recognizerText) =>
        _scheduler.Post(() => ApplyFinal(recognizerText));

    /// <summary>
    /// Prepares the field for a new recording session: optionally clears it, then makes
    /// the recognizer output continue after whatever text remains.
    /// </summary>
    /// <returns><c>true</c> when the transcript was cleared.</returns>
    public bool BeginRecordingSession(bool clearTextOnSessionStart)
    {
        // Kept text wins over the setting while the user has been typing, and speech
        // never clears a field the user is editing by hand.
        var cleared = clearTextOnSessionStart && !_keepTextOnNextRecording && !_isManualInputEnabled;
        if (cleared)
            Publish("");

        _keepTextOnNextRecording = false;
        _lastRecognizerText = "";
        ResumeAfterCurrentText();
        return cleared;
    }

    /// <summary>
    /// Manual input takes over the transcript field: speech results are ignored while it
    /// is on, and the next recording appends to the (possibly edited) text instead of
    /// starting a clean field.
    /// </summary>
    public void SetManualInput(bool enabled)
    {
        if (_isManualInputEnabled == enabled)
            return;

        _isManualInputEnabled = enabled;
        if (enabled)
        {
            _keepTextOnNextRecording = true;
            return;
        }

        DropPendingPartial();
        ResumeAfterCurrentText();
    }

    /// <summary>
    /// Restarts the speech baseline after capture was torn down and recreated, so the
    /// new session's results are appended after the existing transcript.
    /// </summary>
    public void StartNewSpeechSegment()
    {
        _lastRecognizerText = "";
        ResumeAfterCurrentText();
    }

    private void ApplyPartial(string recognizerText)
    {
        _lastRecognizerText = recognizerText;
        if (_isManualInputEnabled)
            return;

        _pendingPartial = recognizerText;
        _hasPendingPartial = true;

        // Start a window only when none is pending; the timer is non-repeating and shows
        // the newest text when it fires.
        if (!_partialThrottle.IsRunning)
            _partialThrottle.Start();
    }

    private void ApplyFinal(string recognizerText)
    {
        _lastRecognizerText = recognizerText;
        if (_isManualInputEnabled)
            return;

        // The final result supersedes any queued partial: drop the window so a stale
        // partial can never overwrite the committed text.
        DropPendingPartial();

        Publish(recognizerText);
        FinalCommitted?.Invoke(recognizerText);
    }

    private void FlushPendingPartial()
    {
        if (!_hasPendingPartial)
            return;

        _hasPendingPartial = false;
        var text = _pendingPartial;
        _pendingPartial = "";

        if (_isManualInputEnabled)
            return;

        Publish(text);
    }

    private void DropPendingPartial()
    {
        _partialThrottle.Stop();
        _hasPendingPartial = false;
        _pendingPartial = "";
    }

    private void Publish(string recognizerText)
    {
        // Raised unconditionally: the observer decides whether anything changed (the
        // FloatingText setter already ignores identical text), and the translation feed
        // must see every committed revision.
        TextChanged?.Invoke(_composer.Compose(recognizerText));
    }

    /// <summary>Speech from now on is appended after the text currently in the field.</summary>
    private void ResumeAfterCurrentText() =>
        _composer.ContinueAfter(_readTranscript(), _lastRecognizerText);

    public void Dispose() => _partialThrottle.Dispose();
}
