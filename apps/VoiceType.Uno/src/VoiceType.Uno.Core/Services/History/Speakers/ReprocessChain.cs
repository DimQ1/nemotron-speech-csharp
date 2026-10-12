using System.Text.Json;

namespace VoiceType.Uno.History;

/// <summary>What a Reprocess chain does (design §11.1): re-recognize → detect speakers → analyze. Any step can be off.</summary>
public sealed record ReprocessOptions
{
    // 1 · Speech
    public bool Rerecognize { get; init; } = true;
    public string? ModelFolder { get; init; }
    public string? CatalogRepoId { get; init; }

    /// <summary>Recognition language; null/"keep" = the language of each recording's current version.</summary>
    public string? Language { get; init; }
    public Dictionary<string, JsonElement>? Settings { get; init; }

    // 2 · Speakers
    public bool Diarize { get; init; } = true;
    public DiarizationJobParameters? Diarization { get; init; }

    // 3 · Analysis
    public bool Analyze { get; init; } = true;
    public AnalysisJobParameters? Analysis { get; init; }

    /// <summary>Make the new version current when the whole chain is over (a re-recognition step is needed).</summary>
    public bool MakeCurrent { get; init; } = true;

    /// <summary>Start condition shared by every step of the chain (null = now).</summary>
    public JobCondition? Condition { get; init; }

    /// <summary><c>user</c> | <c>auto</c> | <c>liveFallback</c>.</summary>
    public string Origin { get; init; } = ReprocessOrigins.User;

    public int Priority { get; init; } = JobPriorities.Normal;

    /// <summary>Version the speaker and analysis steps work on when there is no re-recognition step; null = the current version, pinned at enqueue time.</summary>
    public string? SourceVersion { get; init; }
}

public static class ReprocessOrigins
{
    public const string User = "user";
    public const string Auto = "auto";
    public const string LiveFallback = "liveFallback";
}

/// <summary>
/// Reprocess chains (design §11): <c>rerecognize → diarize → analyze</c> as ordinary queue jobs linked by
/// <see cref="HistoryJob.ChainId"/> and <see cref="HistoryJob.DependsOn"/>. The scheduler keeps a step waiting
/// (<see cref="JobWaitReason.WaitingForPreviousStep"/>) until its predecessor completed; a failed or cancelled step cancels the
/// steps after it and Retry brings them back (<see cref="JobQueue"/>); every step hands the version it produced to the next
/// one. The new version only becomes current at the end of the chain (<see cref="ApplyFinishedChains"/>), so nobody sees text
/// without speakers that is about to change.
/// </summary>
public sealed class ReprocessChain
{
    private readonly JobQueue _queue;
    private readonly RecordingStore _store;
    private readonly Lock _gate = new();
    private bool _attached;

    public ReprocessChain(JobQueue queue, RecordingStore store)
    {
        _queue = queue;
        _store = store;
    }

    /// <summary>
    /// Queues one chain per recording (one batch when there are several, the recordings processed in the order given) with
    /// the steps <paramref name="options"/> switches on. Returns every job in queue order.
    /// </summary>
    /// <exception cref="ArgumentException">No step is switched on, or no recording was given.</exception>
    public IReadOnlyList<HistoryJob> Enqueue(IEnumerable<string> recordingIds, ReprocessOptions options)
    {
        ArgumentNullException.ThrowIfNull(recordingIds);
        ArgumentNullException.ThrowIfNull(options);
        var ids = recordingIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            throw new ArgumentException("Nothing to reprocess.", nameof(recordingIds));
        if (!options.Rerecognize && !options.Diarize && !options.Analyze)
            throw new ArgumentException("Switch on at least one step.", nameof(options));

        var chains = new List<(string, IReadOnlyList<JobQueue.ChainStep>)>();
        foreach (var id in ids)
            chains.Add((id, BuildSteps(id, options)));
        var batchId = ids.Count > 1 ? "b" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5)).ToLowerInvariant() : null;
        var priority = options.Origin == ReprocessOrigins.Auto ? JobPriorities.Low : options.Priority;
        return _queue.EnqueueChains(chains, options.Condition, priority, batchId).SelectMany(c => c).ToList();
    }

    private List<JobQueue.ChainStep> BuildSteps(string recordingId, ReprocessOptions options)
    {
        var names = new List<string>();
        if (options.Rerecognize) names.Add("rerecognize");
        if (options.Diarize) names.Add("diarize");
        if (options.Analyze) names.Add("analyze");

        var manifest = _store.Get(recordingId)?.Manifest
            ?? throw new InvalidOperationException($"Recording {recordingId} no longer exists.");
        var steps = new List<JobQueue.ChainStep>();
        var later = options.Diarize || options.Analyze;

        if (options.Rerecognize)
        {
            steps.Add(new JobQueue.ChainStep(HistoryJobKind.Rerecognize, new JobParameters
            {
                ModelFolder = options.ModelFolder,
                CatalogRepoId = options.CatalogRepoId,
                Language = options.Language,
                Settings = options.Settings,
                // Alone, the step makes its version current itself; in a chain the end of the chain does.
                MakeCurrent = !later && options.MakeCurrent,
                MakeCurrentAtChainEnd = later && options.MakeCurrent ? true : null,
                Origin = options.Origin,
                ChainSteps = names,
            }));
        }

        var pinned = options.Rerecognize ? null : options.SourceVersion ?? manifest.CurrentVersion;
        if (options.Diarize)
        {
            steps.Add(new JobQueue.ChainStep(HistoryJobKind.Diarize, new JobParameters
            {
                SourceVersion = pinned,
                Diarization = options.Diarization ?? new DiarizationJobParameters { Preset = "offline", ReuseExistingRun = true },
                Origin = options.Origin,
                ChainSteps = names,
            }));
        }
        if (options.Analyze)
        {
            steps.Add(new JobQueue.ChainStep(HistoryJobKind.Analyze, new JobParameters
            {
                SourceVersion = pinned,
                Analysis = options.Analysis ?? new AnalysisJobParameters(),
                Origin = options.Origin,
                ChainSteps = names,
            }));
        }
        return steps;
    }

    // ── End of chain ────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to the queue: whenever a chain is over, its re-recognized version becomes current (when the chain asked for
    /// it). Call once at startup; safe to call again.
    /// </summary>
    public void Attach()
    {
        lock (_gate)
        {
            if (_attached)
                return;
            _attached = true;
        }
        _queue.Changed += OnQueueChanged;
        OnQueueChanged();
    }

    private void OnQueueChanged()
    {
        try
        {
            ApplyFinishedChains();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            Console.WriteLine($"[VoiceType.Uno] Reprocess chain end failed: {ex.Message}");
        }
    }

    /// <summary>
    /// For every chain whose steps are all finished: switches the recording to the version its re-recognition created
    /// (<see cref="JobParameters.MakeCurrentAtChainEnd"/>) — also when a later step failed, because the new text is valuable on
    /// its own; the failed step has Retry. A chain the user cancelled leaves the current version alone. Returns how many
    /// recordings switched.
    /// </summary>
    public int ApplyFinishedChains()
    {
        if (_queue.IsReadOnly)
            return 0;
        var switched = 0;
        foreach (var chain in _queue.Jobs.Where(j => j.ChainId is not null).GroupBy(j => j.ChainId!))
        {
            var steps = chain.ToList();
            if (steps.Any(s => !s.IsFinished))
                continue;
            foreach (var rerecognize in steps.Where(s => s.Kind == HistoryJobKind.Rerecognize
                         && s.State == JobState.Completed && s.Parameters.MakeCurrentAtChainEnd == true && s.ChainApplied != true))
            {
                var userCancelled = steps.Any(s => s.State == JobState.Cancelled && s.Error != JobQueue.PreviousStepFailed);
                if (!userCancelled && rerecognize.ResultId is { } version
                    && _store.Get(rerecognize.RecordingId)?.Manifest.FindVersion(version) is not null)
                {
                    _store.SetCurrentVersion(rerecognize.RecordingId, version);
                    switched++;
                }
                _queue.MarkChainApplied(rerecognize.Id);
            }
        }
        return switched;
    }

    /// <summary>
    /// The chain of an auto-reprocessing profile for a new recording (design §11.5): the re-recognition step is skipped when
    /// the profile's model is the one that made the live version; recordings shorter than <see cref="ReprocessSettings.MinDurationSec"/>
    /// get no speakers or analysis. Returns the jobs, or an empty list when nothing is left to do.
    /// </summary>
    public IReadOnlyList<HistoryJob> EnqueueAuto(string recordingId, ReprocessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var manifest = _store.Get(recordingId)?.Manifest;
        if (manifest is null || manifest.Mode.InjectText)
            return [];
        var profile = settings.Profile;
        var longEnough = manifest.Audio?.DurationSec >= settings.MinDurationSec;
        var sameModel = string.IsNullOrWhiteSpace(profile.AsrModelFolder)
            || string.Equals(manifest.Current?.Model?.Folder, profile.AsrModelFolder, StringComparison.OrdinalIgnoreCase);
        var options = new ReprocessOptions
        {
            Rerecognize = profile.Rerecognize && !sameModel,
            ModelFolder = profile.AsrModelFolder,
            Diarize = profile.Diarize && longEnough,
            Analyze = profile.Analyze && longEnough,
            MakeCurrent = profile.MakeCurrent,
            Condition = settings.AutoCondition,
            Origin = ReprocessOrigins.Auto,
        };
        if (!options.Rerecognize && !options.Diarize && !options.Analyze)
            return [];
        return Enqueue([recordingId], options);
    }
}
