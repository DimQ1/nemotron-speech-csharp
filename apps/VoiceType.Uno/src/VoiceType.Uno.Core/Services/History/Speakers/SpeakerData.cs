using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

// Data model of speaker diarization (design docs/design/voicetype-uno-history §10.4): an immutable speaker
// LAYER next to a transcript version. Everything here is additive and forward compatible: every class keeps
// [JsonExtensionData], the manifest schema is not bumped, and an app that does not know the fields round-trips them.

/// <summary>Values of <see cref="DiarizationRunInfo.Source"/>.</summary>
public static class DiarizationSources
{
    public const string Live = "live";
    public const string Job = "job";
}

/// <summary>Which model run produced a diarization run (the ASR <see cref="ModelInfo"/> plus the upstream model).</summary>
public sealed class DiarizationModelInfo
{
    public string? CatalogRepoId { get; set; }
    public string? Upstream { get; set; }
    public string? UpstreamRevision { get; set; }
    public string? Folder { get; set; }
    public string? Title { get; set; }
    public string? Precision { get; set; }
    public string? Family { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Streaming geometry of a run, in 80 ms frames (as on the model card).</summary>
public sealed class DiarizationModeInfo
{
    public string? Preset { get; set; }
    public double LatencySec { get; set; }
    public int SpkcacheLen { get; set; }
    public int FifoLen { get; set; }
    public int ChunkLen { get; set; }
    public int RightContext { get; set; }
    public int UpdatePeriod { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Post-processing of the probabilities into turns (stored so a run can be re-labelled without the model).</summary>
public sealed class DiarizationPostInfo
{
    public double Threshold { get; set; } = 0.5;
    public double MinSpeechSec { get; set; } = 0.25;
    public double MinSilenceSec { get; set; } = 0.2;
    public int MaxSpeakers { get; set; } = 8;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class DiarizationRunStats
{
    public int Speakers { get; set; }
    public double? OverlapSec { get; set; }
    public double? Rtf { get; set; }
    public double? ElapsedSec { get; set; }
    public double? FellBehindAtSec { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>manifest.diarizationRuns[]</c>: one run of the model over the audio (or a live stream).</summary>
public sealed class DiarizationRunInfo
{
    public string Id { get; set; } = "";

    /// <summary>Relative path, e.g. <c>diarization/r1.json</c>.</summary>
    public string File { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>One of <see cref="DiarizationSources"/>.</summary>
    public string Source { get; set; } = DiarizationSources.Job;
    public string? JobId { get; set; }
    public DiarizationModelInfo? Model { get; set; }
    public DiarizationModeInfo? Mode { get; set; }
    public DiarizationPostInfo? Post { get; set; }
    public Dictionary<string, JsonElement>? Settings { get; set; }
    public AppInfo? App { get; set; }
    public DiarizationRunStats? Stats { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>manifest.speakerLayers[]</c>: the labelling of one version by one run.</summary>
public sealed class SpeakerLayerInfo
{
    public string Id { get; set; } = "";

    /// <summary>The transcript version that was labelled.</summary>
    public string Version { get; set; } = "";

    /// <summary>The <see cref="DiarizationRunInfo.Id"/> that supplied the speakers.</summary>
    public string Run { get; set; } = "";

    /// <summary>Relative path, e.g. <c>speakers/v2.r2.json</c>.</summary>
    public string File { get; set; } = "";

    /// <summary>Version of the <see cref="SpeakerLayerBuilder"/> algorithm; a newer algorithm builds a new layer.</summary>
    public int Assigner { get; set; } = SpeakerLayerBuilder.AssignerVersion;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// <see cref="WordTimingKinds.Decoder"/> when the words were placed with the recognizer's exact times,
    /// <see cref="WordTimingKinds.Estimated"/> when the times were estimated (boundaries can be off by a word or two:
    /// the UI says "approximate").
    /// </summary>
    public string WordTiming { get; set; } = WordTimingKinds.Estimated;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A channel of a run folded into a person by Merge (kept so Split can undo it).</summary>
public sealed class SpeakerChannelRef
{
    public string Run { get; set; } = "";
    public int Speaker { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A person that was merged into another one: enough to restore it.</summary>
public sealed class MergedPersonInfo
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int Color { get; set; }
    public int Number { get; set; }

    /// <summary>Position in <c>people</c> before the merge.</summary>
    public int Index { get; set; }
    public List<SpeakerChannelRef> Channels { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>manifest.speakers.people[]</c>.</summary>
public sealed class SpeakerPersonInfo
{
    public string Id { get; set; } = "";

    /// <summary>Null → "Speaker N" by order of appearance.</summary>
    public string? Name { get; set; }

    /// <summary>Palette index 0..7.</summary>
    public int Color { get; set; }

    /// <summary>Arrival number for the default name "Speaker N"; stable when other people are merged away (0 = unset).</summary>
    public int Number { get; set; }

    /// <summary>People merged into this one, oldest first (Split restores the last).</summary>
    public List<MergedPersonInfo>? Merged { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>"Change speaker" of the user: by time, so it survives a new version.</summary>
public sealed class SpeakerOverrideInfo
{
    public double From { get; set; }
    public double To { get; set; }
    public string Person { get; set; } = "";
    public DateTimeOffset At { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A name the analysis suggests for a channel; applied only by the user.</summary>
public sealed class SuggestedSpeakerName
{
    public string Run { get; set; } = "";
    public int Speaker { get; set; }
    public string Name { get; set; } = "";
    public string? Analysis { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>manifest.speakers</c>: people of the recording and the channel → person map of every run.</summary>
public sealed class SpeakerDirectoryInfo
{
    public List<SpeakerPersonInfo> People { get; set; } = [];

    /// <summary>run id → (channel number as text → person id).</summary>
    public Dictionary<string, Dictionary<string, string>> Map { get; set; } = [];
    public List<SpeakerOverrideInfo> Overrides { get; set; } = [];
    public List<SuggestedSpeakerName> Suggested { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>manifest.analyses[]</c>: the Analyze step of a Reprocess chain.</summary>
public sealed class AnalysisInfo
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string Version { get; set; } = "";
    public string? SpeakerLayer { get; set; }
    public TranslationEngineInfo? Engine { get; set; }
    public int PromptVersion { get; set; }

    /// <summary>Person id → name at generation time (the UI says "Names changed after this summary" when it differs).</summary>
    public Dictionary<string, string?>? NamesSnapshot { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><c>complete</c> | <c>partial</c>.</summary>
    public string Status { get; set; } = "complete";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>version.origin</c>: the Reprocess chain that created a version (written once).</summary>
public sealed class VersionOrigin
{
    public string? ChainId { get; set; }

    /// <summary><c>user</c> | <c>auto</c>.</summary>
    public string? Trigger { get; set; }
    public List<string>? Steps { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

// ── Files ───────────────────────────────────────────────────────────────────

public sealed class RunTurn
{
    [JsonPropertyName("s")]
    public double Start { get; set; }

    [JsonPropertyName("e")]
    public double End { get; set; }

    [JsonPropertyName("spk")]
    public int Speaker { get; set; }

    /// <summary>Seconds of the turn during which another speaker was active as well.</summary>
    [JsonPropertyName("ov")]
    public double? OverlapSec { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Per-frame speaker probabilities packed as bytes (8 per frame, base64): about 360 KB per hour at 80 ms.</summary>
public sealed class RunProbabilities
{
    public const string U8Base64 = "u8-base64";

    public double FrameSec { get; set; } = 0.08;
    public int Speakers { get; set; } = 8;
    public string Encoding { get; set; } = U8Base64;
    public string Data { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>diarization/rM.json</c>: model output after post-processing; independent of any transcript version.</summary>
public sealed class DiarizationRunDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = "";
    public double DurationSec { get; set; }
    public List<RunTurn> Turns { get; set; } = [];
    public RunProbabilities? Probs { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SpeakerLayerSegment
{
    /// <summary><see cref="TranscriptSegment.Index"/>.</summary>
    [JsonPropertyName("i")]
    public int Index { get; set; }

    /// <summary>Majority channel of the segment (-1 unknown).</summary>
    [JsonPropertyName("spk")]
    public int Speaker { get; set; } = -1;

    /// <summary>Channel of every word, -1 unknown.</summary>
    [JsonPropertyName("words")]
    public List<int> Words { get; set; } = [];

    /// <summary>[word index, second channel] of overlapped words.</summary>
    [JsonPropertyName("overlap")]
    public List<int[]>? Overlap { get; set; }

    /// <summary>More than one speaker talks inside the segment.</summary>
    [JsonPropertyName("change")]
    public bool? Change { get; set; }

    /// <summary>Segment time (so user overrides by time apply without loading the transcript).</summary>
    [JsonPropertyName("s")]
    public double Start { get; set; }

    [JsonPropertyName("e")]
    public double End { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>speakers/vN.rM.json</c>: the channel of every word of version N as labelled by run M.</summary>
public sealed class SpeakerLayerDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Version { get; set; } = "";
    public string Run { get; set; } = "";
    public int Assigner { get; set; } = SpeakerLayerBuilder.AssignerVersion;
    public string WordTiming { get; set; } = WordTimingKinds.Estimated;
    public List<SpeakerLayerSegment> Segments { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Talk statistics of one channel (computed in Core, without Gemma).</summary>
public sealed class SpeakerChannelStats
{
    public double TalkSec { get; set; }
    public int Words { get; set; }
    public int Turns { get; set; }
    public double OverlapSec { get; set; }
    public double LongestTurnSec { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>analysis/vN.sK.insights.json</c>: per-speaker statistics of a version labelled by a layer (and, later, Gemma insights).</summary>
public sealed class SpeakerInsightsDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Version { get; set; } = "";
    public string SpeakerLayer { get; set; } = "";
    public int PromptVersion { get; set; } = 1;

    /// <summary>Run channel (as text) → statistics.</summary>
    public Dictionary<string, SpeakerChannelStats> Stats { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
