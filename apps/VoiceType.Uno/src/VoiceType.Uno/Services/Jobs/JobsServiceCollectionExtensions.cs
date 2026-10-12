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
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
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
