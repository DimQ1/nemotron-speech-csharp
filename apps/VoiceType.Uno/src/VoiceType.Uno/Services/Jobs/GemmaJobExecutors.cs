using SpeechLib.Llm;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Services.Jobs;

/// <summary>Adapts the runner's <see cref="JobExecutionContext"/> to what the Core workers report.</summary>
internal sealed class ContextProgressSink(JobExecutionContext context) : IJobProgressSink
{
    public void Detail(string? detail) => context.ReportDetail(detail);

    public void Progress(double fraction) => context.ReportProgress(fraction);

    public void Checkpoint(JobCheckpoint checkpoint) => context.OfferCheckpoint(() => checkpoint);
}

/// <summary>Where the Gemma executors get the engine: resolved per job, so a model installed later is picked up.</summary>
internal static class GemmaHost
{
    public static ILlmHost Require(Func<ILlmHost?> host) =>
        host() ?? throw new LlmUnavailableException("The Gemma engine is not available in this build.");
}

/// <summary><c>translate</c> jobs: a saved transcript version into another language (<see cref="TranslateJobWorker"/>).</summary>
public sealed class TranslateJob(RecordingStore store, Func<ILlmHost?> host) : IJobExecutor
{
    public HistoryJobKind Kind => HistoryJobKind.Translate;

    public Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken) =>
        new TranslateJobWorker(store, GemmaHost.Require(host)).RunAsync(job, new ContextProgressSink(context), cancellationToken);
}

/// <summary><c>autoTitle</c> jobs: a short Gemma title for a saved recording (<see cref="AutoTitleJobWorker"/>).</summary>
public sealed class AutoTitleJob(RecordingStore store, Func<ILlmHost?> host) : IJobExecutor
{
    public HistoryJobKind Kind => HistoryJobKind.AutoTitle;

    public Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken) =>
        new AutoTitleJobWorker(store, GemmaHost.Require(host)).RunAsync(job, new ContextProgressSink(context), cancellationToken);
}

/// <summary><c>summarize</c> jobs: map-reduce summary pieces under <c>analysis/</c> for chats (<see cref="SummarizeJobWorker"/>).</summary>
public sealed class SummarizeJob(RecordingStore store, Func<ILlmHost?> host) : IJobExecutor
{
    public HistoryJobKind Kind => HistoryJobKind.Summarize;

    public Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken) =>
        new SummarizeJobWorker(store, GemmaHost.Require(host)).RunAsync(job, new ContextProgressSink(context), cancellationToken);
}
