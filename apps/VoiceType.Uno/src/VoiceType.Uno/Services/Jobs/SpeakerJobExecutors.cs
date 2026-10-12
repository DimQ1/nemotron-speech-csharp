using SpeechLib.Llm;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Services.Jobs;

/// <summary>
/// <c>diarize</c> jobs: speaker detection over a stored recording (<see cref="DiarizeJobWorker"/>, design §10). The model
/// (<see cref="IDiarizerFactory"/>) is loaded per job and unloaded when the job ends.
/// </summary>
public sealed class DiarizeJob(RecordingStore store, IDiarizerFactory factory, Func<HistorySettings> settings) : IJobExecutor
{
    public HistoryJobKind Kind => HistoryJobKind.Diarize;

    public Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken) =>
        new DiarizeJobWorker(store, factory, settings, isPhone: OperatingSystem.IsAndroid())
            .RunAsync(job, new ContextProgressSink(context), cancellationToken);
}

/// <summary><c>analyze</c> jobs: talk statistics, then the Gemma summary and title of the version (<see cref="AnalyzeJobWorker"/>).</summary>
public sealed class AnalyzeJob(RecordingStore store, Func<ILlmHost?> host) : IJobExecutor
{
    public HistoryJobKind Kind => HistoryJobKind.Analyze;

    public Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken) =>
        new AnalyzeJobWorker(store, host).RunAsync(job, new ContextProgressSink(context), cancellationToken);
}
