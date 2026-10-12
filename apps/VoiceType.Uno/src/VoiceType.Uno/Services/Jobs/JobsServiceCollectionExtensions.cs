using Microsoft.Extensions.DependencyInjection.Extensions;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Services.Jobs;

public static class JobsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the history job runner and its parts: power state, recording activity,
    /// keep-alive (Android foreground service), the re-recognition executor and
    /// <see cref="JobRunner"/>. Needs <see cref="JobQueue"/>, <see cref="RecordingStore"/> and
    /// <see cref="SettingsStore"/> registered. Start the runner after the host is built:
    /// <c>App.Services.GetRequiredService&lt;JobRunner&gt;().Start();</c>
    /// </summary>
    public static IServiceCollection AddHistoryJobs(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceConditions>(_ => DeviceConditions.Create());
        // MainViewModel / RecognitionService call RecordingActivity.Report(true/false).
        services.AddSingleton<RecordingActivity>();
        services.AddSingleton<IRecordingActivity>(sp => sp.GetRequiredService<RecordingActivity>());
#if __ANDROID__
        services.AddSingleton<IJobsKeepAlive, VoiceType.Uno.Droid.AndroidJobsKeepAlive>();
#else
        services.AddSingleton<IJobsKeepAlive, NullJobsKeepAlive>();
#endif
        services.AddSingleton<IJobExecutor>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new OfflineRecognitionJob(sp.GetRequiredService<RecordingStore>(), () => settings.Snapshot());
        });
        // Gemma jobs (Background priority on the shared ILlmHost, resolved per job).
        services.AddSingleton<IJobExecutor>(sp =>
            new TranslateJob(sp.GetRequiredService<RecordingStore>(), () => sp.GetService<SpeechLib.Llm.ILlmHost>()));
        services.AddSingleton<IJobExecutor>(sp =>
            new AutoTitleJob(sp.GetRequiredService<RecordingStore>(), () => sp.GetService<SpeechLib.Llm.ILlmHost>()));
        services.AddSingleton<IJobExecutor>(sp =>
            new SummarizeJob(sp.GetRequiredService<RecordingStore>(), () => sp.GetService<SpeechLib.Llm.ILlmHost>()));
        // Speaker detection (design section 10-11): the Diarize and Analyze executors, the people/layers service,
        // and the Reprocess chain API (rerecognize -> diarize -> analyze). The ONNX model is found under the model roots.
        services.AddSingleton<IDiarizerFactory>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new OnnxDiarizerFactory(() => InstalledAsrModels.DefaultRoots(settings.Snapshot()));
        });
        services.AddSingleton<IJobExecutor>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new DiarizeJob(sp.GetRequiredService<RecordingStore>(), sp.GetRequiredService<IDiarizerFactory>(), () => settings.Current.History);
        });
        services.AddSingleton<IJobExecutor>(sp =>
            new AnalyzeJob(sp.GetRequiredService<RecordingStore>(), () => sp.GetService<SpeechLib.Llm.ILlmHost>()));
        // One instance behind both types: ISpeakerService for the contract, SpeakerService for ChangeSegmentSpeaker / GetLayerInfo.
        services.AddSingleton<ISpeakerService>(sp => sp.GetRequiredService<SpeakerService>());
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new SpeakerService(
                sp.GetRequiredService<RecordingStore>(), sp.GetRequiredService<JobQueue>(),
                sp.GetRequiredService<IDiarizerFactory>(), () => settings.Current.History,
                poke: () => sp.GetRequiredService<JobRunner>().Poke());
        });
        services.AddSingleton(sp => new ReprocessChain(sp.GetRequiredService<JobQueue>(), sp.GetRequiredService<RecordingStore>()));
#if DEBUG
        // Scripted host for UI checks without the 2.4 GB model: VOICETYPE_FAKE_LLM=1. Any real ILlmHost registration wins.
        if (Environment.GetEnvironmentVariable("VOICETYPE_FAKE_LLM") == "1")
            services.TryAddSingleton<SpeechLib.Llm.ILlmHost, DebugFakeLlmHost>();
#endif
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            // A finished chain switches its recording to the new version (design 11.2); attached once, with the runner.
            sp.GetRequiredService<ReprocessChain>().Attach();
            return new JobRunner(
                sp.GetRequiredService<JobQueue>(),
                sp.GetServices<IJobExecutor>(),
                sp.GetRequiredService<IDeviceConditions>(),
                sp.GetRequiredService<IRecordingActivity>(),
                () => settings.Current.History,
                sp.GetRequiredService<IJobsKeepAlive>());
        });
        return services;
    }
}
