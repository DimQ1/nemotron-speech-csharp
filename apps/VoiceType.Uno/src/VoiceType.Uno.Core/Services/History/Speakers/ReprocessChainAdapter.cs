namespace VoiceType.Uno.History;

/// <summary>
/// What the Reprocess sheet asks for (design §11.1): the steps run in order Speech → Speakers → Analysis.
/// </summary>
/// <param name="RecordingIds">Recordings with audio.</param>
/// <param name="Recognize">Parameters of the Re-recognize step; null = the step is off (a chain over the current version).</param>
/// <param name="Condition">When the chain runs.</param>
/// <param name="Speakers">Detect speakers after recognition.</param>
/// <param name="SpeakersPreset"><c>offline</c> (best quality) or <c>fast</c>.</param>
/// <param name="Analysis">Gemma analysis (summary, action items, talk time) at the end.</param>
public sealed record ReprocessChainRequest(IReadOnlyList<string> RecordingIds, JobParameters? Recognize, JobCondition Condition,
    bool Speakers, string SpeakersPreset, bool Analysis);

/// <summary>
/// Adapter the Reprocess sheet calls. Agent X1 implements it over <c>ReprocessChain.Enqueue(...)</c> and registers it
/// (<c>services.AddSingleton&lt;IReprocessChainEnqueuer, …&gt;()</c>); while none is registered the sheet hides the chain
/// controls and keeps its single Re-recognize job. Debug builds register a fake so the sheet can be checked.
/// </summary>
public interface IReprocessChainEnqueuer
{
    /// <summary>The queue accepts chains (schema 2) and the steps are installed.</summary>
    bool IsAvailable { get; }

    /// <summary>Enqueues one chain per recording (a batch for several) and returns the jobs created.</summary>
    IReadOnlyList<HistoryJob> Enqueue(ReprocessChainRequest request);
}
