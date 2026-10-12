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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class HistoryJob
{
    public string Id { get; set; } = "";
    public HistoryJobKind Kind { get; set; }

    /// <summary>Shared by jobs enqueued together from a multi-selection.</summary>
    public string? BatchId { get; set; }
    public string RecordingId { get; set; } = "";
    public JobParameters Parameters { get; set; } = new();

    /// <summary>Higher runs first; FIFO (queue order) within a priority.</summary>
    public int Priority { get; set; }
    public JobCondition Condition { get; set; } = new();
    public JobState State { get; set; } = JobState.Queued;

    /// <summary>0..1.</summary>
    public double Progress { get; set; }
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

/// <summary><c>jobs/queue.json</c>. List order is the FIFO order.</summary>
public sealed class JobQueueDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public List<HistoryJob> Jobs { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
