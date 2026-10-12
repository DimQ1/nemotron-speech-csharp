using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

[JsonConverter(typeof(LenientEnumConverter<HistoryJobKind>))]
public enum HistoryJobKind
{
    /// <summary>Written by a newer app; never scheduled.</summary>
    Unknown = 0,
    Rerecognize,
    Translate,
    AutoTitle,
    Summarize,
}

[JsonConverter(typeof(LenientEnumConverter<JobConditionKind>))]
public enum JobConditionKind
{
    /// <summary>Written by a newer app; never satisfied.</summary>
    Unknown = 0,
    Now,
    /// <summary>Waits until no live recording is running.</summary>
    WhenIdle,
    /// <summary>Waits for the charger (phones; desktops always qualify).</summary>
    WhenCharging,
    /// <summary>Waits for <see cref="JobCondition.NotBefore"/> and/or a daily time window.</summary>
    AtTime,
}

[JsonConverter(typeof(LenientEnumConverter<JobState>))]
public enum JobState
{
    /// <summary>Unrecognized persisted state; treated as paused.</summary>
    Unknown = 0,
    Queued,
    /// <summary>Display-only: queued but its condition is not met (see <see cref="JobScheduler.GetWaitReason"/>).</summary>
    Waiting,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public static class JobPriorities
{
    public const int Low = -10;
    public const int Normal = 0;
    public const int High = 10;
}

/// <summary>When a queued job may start.</summary>
public sealed class JobCondition
{
    public JobConditionKind Kind { get; set; } = JobConditionKind.Now;

    /// <summary><see cref="JobConditionKind.AtTime"/>: earliest absolute start.</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary><see cref="JobConditionKind.AtTime"/>: daily window start (local clock of <c>now</c>).</summary>
    public TimeOnly? WindowStart { get; set; }

    /// <summary>Daily window end (exclusive). A window may cross midnight (22:00–06:00).</summary>
    public TimeOnly? WindowEnd { get; set; }

    /// <summary>
    /// Schema 2: also wait for the charger, on top of <see cref="Kind"/> (conditions are a
    /// conjunction, e.g. "01:00–06:00 and charging"). Ignored where nothing runs on battery.
    /// </summary>
    public bool RequireCharging { get; set; }

    /// <summary>Schema 2: also wait until no live recording runs, on top of <see cref="Kind"/>.</summary>
    public bool RequireIdle { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static JobCondition Now() => new() { Kind = JobConditionKind.Now };
    public static JobCondition WhenIdle() => new() { Kind = JobConditionKind.WhenIdle };
    public static JobCondition WhenCharging() => new() { Kind = JobConditionKind.WhenCharging };
    public static JobCondition At(DateTimeOffset notBefore) => new() { Kind = JobConditionKind.AtTime, NotBefore = notBefore };
    public static JobCondition Window(TimeOnly start, TimeOnly end) =>
        new() { Kind = JobConditionKind.AtTime, WindowStart = start, WindowEnd = end };
}

/// <summary>What a job runs with. Shared by all jobs of one batch.</summary>
public sealed class JobParameters
{
    /// <summary>Recognizer model folder name (rerecognize).</summary>
    public string? ModelFolder { get; set; }
    public string? CatalogRepoId { get; set; }

    /// <summary>Requested recognition language (rerecognize).</summary>
    public string? Language { get; set; }

    /// <summary>Recognition settings that affect the result.</summary>
    public Dictionary<string, JsonElement>? Settings { get; set; }

    /// <summary>Translation target language (translate).</summary>
    public string? TargetLanguage { get; set; }

    /// <summary>Version to process; null = the current version when the job starts.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>Rerecognize: make the new version current when the job completes (null = no).</summary>
    public bool? MakeCurrent { get; set; }

    /// <summary>AutoTitle: generate even when a title (Gemma's or the user's) exists. The user title is still never replaced.</summary>
    public bool? Regenerate { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class HistoryJob
{
    public string Id { get; set; } = "";
    public HistoryJobKind Kind { get; set; }

    /// <summary>Shared by jobs enqueued together from a multi-selection.</summary>
    public string? BatchId { get; set; }

    /// <summary>Schema 2: steps of one Reprocess chain (design §11.4). Not executed as a chain yet.</summary>
    public string? ChainId { get; set; }

    /// <summary>Schema 2: id of the job that must be <see cref="JobState.Completed"/> before this one starts.</summary>
    public string? DependsOn { get; set; }
    public string RecordingId { get; set; } = "";
    public JobParameters Parameters { get; set; } = new();

    /// <summary>Higher runs first; FIFO (queue order) within a priority.</summary>
    public int Priority { get; set; }
    public JobCondition Condition { get; set; } = new();
    public JobState State { get; set; } = JobState.Queued;

    /// <summary>0..1.</summary>
    public double Progress { get; set; }

    /// <summary>Where a paused/interrupted job continues; null = from the start.</summary>
    public JobCheckpoint? Checkpoint { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    /// <summary>Id of what the job produced (e.g. new version <c>v3</c>, translation <c>t2</c>).</summary>
    public string? ResultId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
}

/// <summary>
/// Resume point of a job, persisted with the queue (design §9.1). Rerecognize: the sample
/// where the last committed phrase ended plus a file holding the segments committed so far.
/// </summary>
public sealed class JobCheckpoint
{
    /// <summary>Audio sample (at the recognizer rate) to continue from.</summary>
    public long Sample { get; set; }

    /// <summary>Full path of the partial result (e.g. <c>jobs/&lt;jobId&gt;.part.json</c>).</summary>
    public string? PartFile { get; set; }

    /// <summary>
    /// Gemma jobs (translate, summarize): index of the next unit to run. Informational: the jobs
    /// resume from their own partial result (the partial translation, <c>analysis/vN.chunks.json</c>),
    /// which is written before the checkpoint and therefore never behind it.
    /// </summary>
    public int? Unit { get; set; }

    /// <summary>Gemma jobs: id of the partial result (translation <c>t2</c>).</summary>
    public string? ArtifactId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>jobs/queue.json</c>. List order is the FIFO order.</summary>
public sealed class JobQueueDocument
{
    /// <summary>
    /// 2: chain fields (<c>chainId</c>, <c>dependsOn</c>), combined conditions, checkpoints and
    /// the whole-queue pause. An app that reads a newer schema opens the queue read-only, because
    /// <see cref="LenientEnumConverter{TEnum}"/> would write job kinds it does not know back as
    /// <c>"unknown"</c> (design §11.4).
    /// </summary>
    public const int CurrentSchema = 2;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Whole-queue pause (⏸ on the Queue page): nothing new starts.</summary>
    public bool Paused { get; set; }

    public List<HistoryJob> Jobs { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
