using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.Uno.History;
using VoiceType.Uno.Services.Jobs;

namespace VoiceType.Uno.Presentation.Queue;

/// <summary>
/// Queue page (design §4.5): the running job with progress/ETA, scheduled cards (condition not
/// met yet), waiting cards (ready, paused or after the running job), failed jobs with Retry, and
/// the "N completed today" line. A batch is one card. Rebuilt on queue changes; the running
/// card's progress is updated in place from <see cref="JobRunner.StatusChanged"/>.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly JobQueue _queue;
    private readonly JobRunner _runner;
    private readonly RecordingStore _store;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, RecordingManifest?> _recordings = new(StringComparer.Ordinal);
    private bool _refreshPending;
    private bool _attached;
    private string? _signature;

    public QueueViewModel(JobQueue queue, JobRunner runner, RecordingStore store, DispatcherQueue dispatcher)
    {
        _queue = queue;
        _runner = runner;
        _store = store;
        _dispatcher = dispatcher;
        TogglePauseAllCommand = new RelayCommand(() => _queue.SetPaused(!_queue.IsPaused), () => !_queue.IsReadOnly);
        ClearFinishedCommand = new RelayCommand(() => _queue.RemoveFinished(includeFailed: true));
    }

    public ObservableCollection<JobCardViewModel> Running { get; } = [];
    public ObservableCollection<JobCardViewModel> Scheduled { get; } = [];
    public ObservableCollection<JobCardViewModel> Waiting { get; } = [];
    public ObservableCollection<JobCardViewModel> Failed { get; } = [];

    public IRelayCommand TogglePauseAllCommand { get; }
    public IRelayCommand ClearFinishedCommand { get; }

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isQueuePaused;
    [ObservableProperty] private string _pauseAllLabel = "Pause the queue";
    [ObservableProperty] private bool _hasRunning;
    [ObservableProperty] private bool _hasScheduled;
    [ObservableProperty] private bool _hasWaiting;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasFinished;
    [ObservableProperty] private string _finishedText = "";
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private string _readOnlyText = "";
    [ObservableProperty] private bool _showPausedNotice;
    [ObservableProperty] private string _runsWhenText = "";

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _queue.Changed += OnQueueChanged;
        _store.Changed += OnRecordingChanged;
        _runner.StatusChanged += OnRunnerStatusChanged;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        _queue.Changed -= OnQueueChanged;
        _store.Changed -= OnRecordingChanged;
        _runner.StatusChanged -= OnRunnerStatusChanged;
    }

    // ── Card actions (called by the cards and the ⋯ menu) ──────────────────

    internal void Pause(JobCardViewModel card) => ForCard(card, _queue.Pause);
    internal void Resume(JobCardViewModel card) => ForCard(card, _queue.Resume);
    internal void Cancel(JobCardViewModel card) => ForCard(card, _queue.Cancel);
    internal void Retry(JobCardViewModel card)
    {
        ForCard(card, _queue.Retry);
        _runner.Poke();
    }

    internal void RunNow(JobCardViewModel card)
    {
        ForCard(card, id => _queue.SetCondition(id, JobCondition.Now()));
        _runner.Poke();
    }

    /// <summary>Moves the card before the previous pending card (or after the next one).</summary>
    internal void Move(JobCardViewModel card, int direction)
    {
        var order = PendingCards(_queue.Jobs);
        var index = order.FindIndex(ids => ids.SequenceEqual(card.JobIds));
        var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count)
            return;

        (order[index], order[target]) = (order[target], order[index]);
        _queue.Reorder(order.SelectMany(ids => ids).ToList());
    }

    internal bool CanMove(JobCardViewModel card, int direction)
    {
        var order = PendingCards(_queue.Jobs);
        var index = order.FindIndex(ids => ids.SequenceEqual(card.JobIds));
        return index >= 0 && index + direction >= 0 && index + direction < order.Count;
    }

    private void ForCard(JobCardViewModel card, Func<string, bool> action)
    {
        foreach (var id in card.JobIds)
            action(id);
    }

    // ── Refresh ─────────────────────────────────────────────────────────────

    private void OnQueueChanged() => ScheduleRefresh();

    private void OnRecordingChanged(string recordingId)
    {
        lock (_recordings)
            _recordings.Remove(recordingId);
        ScheduleRefresh();
    }

    private void OnRunnerStatusChanged()
    {
        _dispatcher.TryEnqueue(() =>
        {
            var status = _runner.Status;
            var card = Running.FirstOrDefault();
            if (status is null || card is null || !card.JobIds.Contains(status.JobId))
            {
                ScheduleRefresh();
                return;
            }
            card.ApplyStatus(status, _runner.CurrentContext().IsLiveRecording);
        });
    }

    private void ScheduleRefresh()
    {
        lock (_recordings)
        {
            if (_refreshPending)
                return;
            _refreshPending = true;
        }
        _dispatcher.TryEnqueue(() =>
        {
            lock (_recordings)
                _refreshPending = false;
            if (_attached)
                Refresh(force: false);
        });
    }

    public void Refresh() => Refresh(force: true);

    /// <summary>
    /// Rebuilds the cards only when the queue's structure changed: progress saves rewrite the
    /// queue every few seconds, and rebuilding then would swallow clicks on the running card.
    /// </summary>
    private void Refresh(bool force)
    {
        var jobs = _queue.Jobs;
        var context = _runner.CurrentContext();
        var now = context.Now;

        var signature = string.Join(";", jobs.Select(j => string.Join("|",
                j.Id, j.State, j.Condition.Kind, j.Condition.NotBefore, j.Condition.WindowStart, j.Condition.WindowEnd,
                j.Condition.RequireCharging, j.Condition.RequireIdle, j.Priority, j.Error)))
            + string.Join("|", _queue.IsPaused, context.IsLiveRecording, context.IsCharging, context.AllowDuringRecording,
                now.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture));
        if (!force && signature == _signature)
        {
            if (Running.FirstOrDefault() is { } card && _runner.Status is { } status && card.JobIds.Contains(status.JobId))
                card.ApplyStatus(status, context.IsLiveRecording);
            return;
        }
        _signature = signature;

        Running.Clear();
        Scheduled.Clear();
        Waiting.Clear();
        Failed.Clear();

        foreach (var group in Group(jobs.Where(j => !j.IsFinished || j.State == JobState.Failed)))
        {
            var lead = group[0];
            var reason = JobScheduler.GetWaitReason(lead, jobs, context);
            // Behind the running job, but its own condition is not met either: that is what to show.
            if (reason == JobWaitReason.AnotherJobRunning)
            {
                var own = context.IsLiveRecording && !context.AllowDuringRecording
                    ? JobWaitReason.LiveRecording
                    : JobScheduler.GetConditionReason(lead.Condition, context);
                if (own != JobWaitReason.None)
                    reason = own;
            }
            var card = new JobCardViewModel(this, group, Recording);
            if (group.Any(j => j.State == JobState.Running))
            {
                card.ConfigureRunning(_runner.Status, context.IsLiveRecording, ShowAndroidWarning: OperatingSystem.IsAndroid());
                Running.Add(card);
            }
            else if (group.All(j => j.State == JobState.Failed))
            {
                card.ConfigureFailed();
                Failed.Add(card);
            }
            else if (IsScheduled(reason))
            {
                // A time condition is already said by its chip.
                var text = reason is JobWaitReason.OutsideWindow or JobWaitReason.BeforeStartTime
                    ? null
                    : JobDisplay.WaitReasonText(lead, reason, now);
                card.ConfigureWaiting(text, scheduled: true, now, _queue.IsReadOnly);
                Scheduled.Add(card);
            }
            else
            {
                card.ConfigureWaiting(JobDisplay.WaitReasonText(lead, reason, now), scheduled: false, now, _queue.IsReadOnly);
                Waiting.Add(card);
            }
        }

        var today = now.Date;
        var finished = jobs.Where(j => j.State is JobState.Completed or JobState.Cancelled).ToList();
        var completedToday = finished.Count(j => j.State == JobState.Completed && j.FinishedAt?.ToOffset(now.Offset).Date == today);
        HasFinished = finished.Count > 0 || Failed.Count > 0;
        FinishedText = completedToday > 0
            ? $"{completedToday} completed today"
            : finished.Count > 0 ? $"{finished.Count} finished" : "Finished jobs";

        var running = jobs.Count(j => j.State == JobState.Running);
        var waiting = jobs.Count(j => j.State is JobState.Queued or JobState.Paused or JobState.Unknown);
        Subtitle = running + waiting == 0
            ? "Nothing to do · one heavy job at a time"
            : $"{running} running · {waiting} waiting · one heavy job at a time";

        IsReadOnly = _queue.IsReadOnly;
        ReadOnlyText = _queue.IsReadOnly
            ? $"This queue was saved by a newer VoiceType (format {_queue.FileSchema}). It is shown read-only and nothing runs, so its jobs are not lost — update the app to run them."
            : "";
        IsQueuePaused = _queue.IsPaused;
        ShowPausedNotice = _queue.IsPaused;
        PauseAllLabel = _queue.IsPaused ? "Resume the queue" : "Pause the queue";
        RunsWhenText = OperatingSystem.IsAndroid()
            ? "Progress is saved: a closed app resumes where it stopped. Jobs run while VoiceType is open or working in the background."
            : "Progress is saved: a closed app resumes where it stopped. Jobs run while VoiceType is running.";

        HasRunning = Running.Count > 0;
        HasScheduled = Scheduled.Count > 0;
        HasWaiting = Waiting.Count > 0;
        HasFailed = Failed.Count > 0;
        IsEmpty = !HasRunning && !HasScheduled && !HasWaiting && !HasFailed;
        TogglePauseAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Condition not met yet (time, charger, idle) → Scheduled; ready / paused / behind the running job → Waiting.</summary>
    private static bool IsScheduled(JobWaitReason reason) => reason is
        JobWaitReason.BeforeStartTime or JobWaitReason.OutsideWindow or JobWaitReason.NotCharging
        or JobWaitReason.LiveRecording or JobWaitReason.WaitingForPreviousStep;

    /// <summary>Jobs grouped into cards: a batch's jobs of the same state class together, others alone.</summary>
    private static List<List<HistoryJob>> Group(IEnumerable<HistoryJob> jobs)
    {
        var cards = new List<List<HistoryJob>>();
        var batches = new Dictionary<string, List<HistoryJob>>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            // The running job of a batch gets its own card; the rest of the batch stays together.
            var key = job.BatchId is null ? null : job.BatchId + "|" + StateClass(job);
            if (key is not null && batches.TryGetValue(key, out var existing))
            {
                existing.Add(job);
                continue;
            }
            var card = new List<HistoryJob> { job };
            cards.Add(card);
            if (key is not null)
                batches[key] = card;
        }
        return cards;
    }

    private static string StateClass(HistoryJob job) => job.State switch
    {
        JobState.Running => "running",
        JobState.Failed => "failed",
        _ => "pending",
    };

    private static List<List<string>> PendingCards(IReadOnlyList<HistoryJob> jobs) =>
        Group(jobs.Where(j => !j.IsFinished && j.State != JobState.Running))
            .Select(g => g.Select(j => j.Id).ToList())
            .ToList();

    private RecordingManifest? Recording(string id)
    {
        lock (_recordings)
        {
            if (_recordings.TryGetValue(id, out var cached))
                return cached;
        }
        RecordingManifest? manifest = null;
        try
        {
            manifest = _store.Get(id)?.Manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        lock (_recordings)
            _recordings[id] = manifest;
        return manifest;
    }
}

/// <summary>One Queue card: a single job or a batch of jobs sharing parameters.</summary>
public sealed partial class JobCardViewModel : ObservableObject
{
    private readonly QueueViewModel _owner;
    private readonly IReadOnlyList<HistoryJob> _jobs;
    private readonly double? _totalAudioSeconds;

    internal JobCardViewModel(QueueViewModel owner, IReadOnlyList<HistoryJob> jobs, Func<string, RecordingManifest?> recording)
    {
        _owner = owner;
        _jobs = jobs;
        JobIds = jobs.Select(j => j.Id).ToList();
        var lead = jobs[0];
        var manifests = jobs.Select(j => recording(j.RecordingId)).ToList();
        IsBatch = jobs.Count > 1;

        var kind = JobDisplay.KindTitle(lead.Kind);
        Title = IsBatch
            ? $"Batch · {kind} {jobs.Count} recordings"
            : $"{kind} · {manifests[0]?.EffectiveTitle ?? "Deleted recording"}";
        Subtitle = DescribeParameters(lead, IsBatch ? null : manifests[0]);
        Lines = IsBatch
            ? manifests.Select(m => m is null ? "Deleted recording" : $"{m.EffectiveTitle} · {JobDisplay.FormatClock(m.Audio?.DurationSec ?? 0)}").ToList()
            : [];
        HasLines = Lines.Count > 0;
        Glyph = lead.Kind == HistoryJobKind.Rerecognize ? "" : ""; // Refresh / Translate-ish
        _totalAudioSeconds = !IsBatch ? manifests[0]?.Audio?.DurationSec : null;

        PauseCommand = new RelayCommand(() => _owner.Pause(this));
        ResumeCommand = new RelayCommand(() => _owner.Resume(this));
        CancelCommand = new RelayCommand(() => _owner.Cancel(this));
        RetryCommand = new RelayCommand(() => _owner.Retry(this));
        RunNowCommand = new RelayCommand(() => _owner.RunNow(this));
    }

    public IReadOnlyList<string> JobIds { get; }
    public bool IsBatch { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public IReadOnlyList<string> Lines { get; }
    public bool HasLines { get; }

    public IRelayCommand PauseCommand { get; }
    public IRelayCommand ResumeCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand RetryCommand { get; }
    public IRelayCommand RunNowCommand { get; }

    [ObservableProperty] private IReadOnlyList<string> _chips = [];
    [ObservableProperty] private bool _hasChips;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _hasStatus;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private bool _showLiveWarning;
    [ObservableProperty] private string _liveWarningText = "";
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canRunNow;
    [ObservableProperty] private bool _isRunning;

    public string CancelLabel => IsBatch ? "Cancel batch" : "Cancel";

    internal bool CanMoveUp => _owner.CanMove(this, -1);
    internal bool CanMoveDown => _owner.CanMove(this, +1);
    internal void MoveUp() => _owner.Move(this, -1);
    internal void MoveDown() => _owner.Move(this, +1);

    internal void ConfigureRunning(JobRunStatus? status, bool isLiveRecording, bool ShowAndroidWarning)
    {
        IsRunning = true;
        ShowProgress = true;
        CanPause = true;
        CanCancel = true;
        LiveWarningText = ShowAndroidWarning
            ? "Live recording in progress · slower, uses battery"
            : "Live recording in progress · slower";
        var running = _jobs.First(j => j.State == JobState.Running);
        if (status is not null && status.JobId == running.Id)
            ApplyStatus(status, isLiveRecording);
        else
        {
            ProgressValue = running.Progress * 100;
            ProgressText = JobDisplay.ProgressLine(running.Progress, _totalAudioSeconds);
            EtaText = "starting…";
            ShowLiveWarning = isLiveRecording;
        }
    }

    internal void ApplyStatus(JobRunStatus status, bool isLiveRecording)
    {
        ProgressValue = status.Fraction * 100;
        var total = status.TotalAudio?.TotalSeconds ?? _totalAudioSeconds;
        ProgressText = status.Detail ?? JobDisplay.ProgressLine(status.Fraction, total);
        EtaText = status.Detail is null ? JobDisplay.FormatEta(status.Remaining) : "";
        ShowLiveWarning = isLiveRecording;
    }

    internal void ConfigureWaiting(string? reason, bool scheduled, DateTimeOffset now, bool readOnly)
    {
        var lead = _jobs[0];
        var chips = JobDisplay.ConditionChips(lead.Condition, now).ToList();
        if (lead.Priority < JobPriorities.Normal)
            chips.Add("Low priority");
        Chips = chips;
        HasChips = chips.Count > 0;
        var paused = _jobs.All(j => j.State == JobState.Paused);
        ShowProgress = lead.Progress > 0 && !IsBatch;
        ProgressValue = lead.Progress * 100;
        if (ShowProgress)
        {
            // Resumes from its checkpoint: show where it stopped.
            ProgressText = JobDisplay.ProgressLine(lead.Progress, _totalAudioSeconds);
            EtaText = paused ? "Paused" : "continues from here";
            StatusText = paused ? null : reason;
        }
        else
        {
            StatusText = paused ? "Paused" : reason;
        }
        HasStatus = !string.IsNullOrEmpty(StatusText);
        if (readOnly)
            return;
        CanResume = _jobs.Any(j => j.State == JobState.Paused);
        CanPause = _jobs.Any(j => j.State == JobState.Queued);
        CanCancel = true;
        CanRunNow = scheduled || lead.Condition.Kind != JobConditionKind.Now;
    }

    internal void ConfigureFailed()
    {
        var lead = _jobs[0];
        StatusText = "Failed: " + (lead.Error ?? "unknown error");
        HasStatus = true;
        CanRetry = true;
    }

    internal static string DescribeParameters(HistoryJob job, RecordingManifest? manifest)
    {
        var p = job.Parameters;
        var parts = new List<string>();
        if (job.Kind == HistoryJobKind.Rerecognize)
        {
            if (!string.IsNullOrEmpty(p.ModelFolder))
                parts.Add(ShortModel(p.ModelFolder));
            parts.Add(p.Language is null or "keep"
                ? (manifest is null ? "keep each language" : "same language")
                : p.Language.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto language" : p.Language.ToUpperInvariant());
            if (p.Settings?.TryGetValue(OfflineRecognitionJob.SettingKeys.UseVad, out var vad) == true)
                parts.Add(vad.ValueKind == System.Text.Json.JsonValueKind.True ? "VAD on" : "VAD off");
            if (manifest is not null)
                parts.Add("creates " + ReprocessPlan.NextVersionId(manifest));
        }
        else if (!string.IsNullOrEmpty(p.TargetLanguage))
        {
            parts.Add("→ " + p.TargetLanguage.ToUpperInvariant());
        }
        return string.Join(" · ", parts);
    }

    /// <summary>"Parakeet TDT 0.6B v3 INT4" from the catalog, else the folder name.</summary>
    private static string ShortModel(string folder) =>
        ModelCatalog.FindBySubfolder(folder) is { } d ? $"{d.CommercialName} {d.Variant}" : folder;
}
