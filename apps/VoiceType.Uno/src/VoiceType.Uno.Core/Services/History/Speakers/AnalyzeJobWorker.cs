using SpeechLib.Llm;

namespace VoiceType.Uno.History;

/// <summary>
/// <c>analyze</c> job, the last step of a Reprocess chain (design §11.3): per-speaker talk statistics of the version (no Gemma,
/// always written when the version has a speaker layer), then the existing Gemma jobs on the same version — the map-reduce
/// summary (<see cref="SummarizeJobWorker"/>, also what chats read) and the title (<see cref="AutoTitleJobWorker"/>). Without the
/// Gemma model the statistics are still saved and the job completes with a note instead of failing a whole chain for a missing
/// optional model.
/// </summary>
public sealed class AnalyzeJobWorker
{
    private readonly RecordingStore _store;
    private readonly Func<ILlmHost?> _host;
    private readonly TimeProvider _time;

    public AnalyzeJobWorker(RecordingStore store, Func<ILlmHost?> host, TimeProvider? time = null)
    {
        _store = store;
        _host = host;
        _time = time ?? TimeProvider.System;
    }

    /// <returns>The id of the analysis (<c>a1</c>) when statistics were written, else the version.</returns>
    public async Task<string?> RunAsync(HistoryJob job, IJobProgressSink sink, CancellationToken cancellationToken)
    {
        var manifest = (_store.Get(job.RecordingId) ?? throw new InvalidOperationException("The recording no longer exists.")).Manifest;
        var versionId = job.Parameters.SourceVersion ?? manifest.CurrentVersion
            ?? throw new InvalidOperationException("The recording has no transcript to analyze.");
        if (manifest.FindVersion(versionId) is null)
            throw new InvalidOperationException($"Transcript {versionId} no longer exists.");
        var analysis = job.Parameters.Analysis ?? new AnalysisJobParameters();

        string? resultId = null;
        if (analysis.SpeakerStats != false && manifest.CurrentSpeakerLayer(versionId) is { } layer)
        {
            sink.Detail("Counting talk time…");
            resultId = WriteStats(job.RecordingId, manifest, layer, analysis).Id;
        }
        sink.Progress(0.1);
        cancellationToken.ThrowIfCancellationRequested();

        var host = _host();
        var gemma = host is not null && host.IsModelInstalled;
        var wantSummary = analysis.Summary != false;
        var wantTitle = analysis.Title != false;
        if ((wantSummary || wantTitle) && !gemma)
        {
            sink.Detail("Summary skipped: the Gemma model is not installed");
            sink.Progress(1);
            return resultId ?? versionId;
        }

        // The Gemma workers read the version from the job; hand them the chain's version, not the current one.
        var pinned = new HistoryJob
        {
            Id = job.Id,
            Kind = job.Kind,
            RecordingId = job.RecordingId,
            Parameters = new JobParameters { SourceVersion = versionId, Regenerate = true },
        };
        if (wantSummary)
        {
            await new SummarizeJobWorker(_store, host!, _time)
                .RunAsync(pinned, new ScaledSink(sink, 0.1, wantTitle ? 0.85 : 1.0), cancellationToken).ConfigureAwait(false);
        }
        if (wantTitle)
        {
            await new AutoTitleJobWorker(_store, host!, _time)
                .RunAsync(pinned, new ScaledSink(sink, wantSummary ? 0.85 : 0.1, 1.0), cancellationToken).ConfigureAwait(false);
        }
        sink.Progress(1);
        return resultId ?? versionId;
    }

    private AnalysisInfo WriteStats(string recordingId, RecordingManifest manifest, SpeakerLayerInfo layer, AnalysisJobParameters analysis)
    {
        var run = _store.LoadDiarizationRun(recordingId, layer.Run);
        var layerDocument = _store.LoadSpeakerLayer(recordingId, layer.Id);
        var directory = manifest.Speakers;
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (directory is not null)
        {
            foreach (var person in directory.People)
                names[person.Id] = person.Name;
        }
        return _store.SaveInsights(recordingId,
            new AnalysisInfo
            {
                SpeakerLayer = layer.Id,
                Engine = null,
                PromptVersion = analysis.PromptVersion,
                NamesSnapshot = names,
                Status = "complete",
            },
            new SpeakerInsightsDocument
            {
                PromptVersion = analysis.PromptVersion,
                Stats = SpeakerViews.ChannelStats(run, layerDocument),
            });
    }

    /// <summary>Maps a worker's 0..1 into a slice of the job's progress.</summary>
    private sealed class ScaledSink(IJobProgressSink inner, double from, double to) : IJobProgressSink
    {
        public void Detail(string? detail) => inner.Detail(detail);
        public void Progress(double fraction) => inner.Progress(from + Math.Clamp(fraction, 0, 1) * (to - from));
        public void Checkpoint(JobCheckpoint checkpoint) => inner.Checkpoint(checkpoint);
    }
}
