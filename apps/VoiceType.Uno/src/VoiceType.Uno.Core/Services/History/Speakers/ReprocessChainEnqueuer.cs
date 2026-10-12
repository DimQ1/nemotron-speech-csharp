namespace VoiceType.Uno.History;

/// <summary>The Reprocess sheet's adapter over <see cref="ReprocessChain"/> (design §11.1).</summary>
public sealed class ReprocessChainEnqueuer : IReprocessChainEnqueuer
{
    private readonly ReprocessChain _chain;
    private readonly JobQueue _queue;

    public ReprocessChainEnqueuer(ReprocessChain chain, JobQueue queue)
    {
        _chain = chain;
        _queue = queue;
    }

    public bool IsAvailable => !_queue.IsReadOnly;

    public IReadOnlyList<HistoryJob> Enqueue(ReprocessChainRequest request)
    {
        var recognize = request.Recognize;
        var options = new ReprocessOptions
        {
            Rerecognize = recognize is not null,
            ModelFolder = recognize?.ModelFolder,
            CatalogRepoId = recognize?.CatalogRepoId,
            Language = recognize?.Language,
            Settings = recognize?.Settings,
            Diarize = request.Speakers,
            Diarization = request.Speakers ? new DiarizationJobParameters { Preset = request.SpeakersPreset } : null,
            Analyze = request.Analysis,
            MakeCurrent = recognize is not null,
            Condition = request.Condition,
        };
        return _chain.Enqueue(request.RecordingIds, options);
    }
}
