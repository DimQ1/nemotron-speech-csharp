using SpeechLib.AudioStorage;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Services;

/// <summary>
/// Startup housekeeping of the recordings history, off the UI thread: finalize
/// recordings a crash left unfinished (<c>.lock</c> / <c>audio.*.part</c>), then apply
/// audio retention. A new live recording waits for <see cref="Recovered"/> so recovery
/// never mistakes it for a crashed one.
/// </summary>
public sealed class HistoryMaintenance
{
    private readonly RecordingStore _store;
    private readonly SettingsStore _settings;
    private Task _recovered = Task.CompletedTask;
    private Task _completed = Task.CompletedTask;

    public HistoryMaintenance(RecordingStore store, SettingsStore settings)
    {
        _store = store;
        _settings = settings;
    }

    /// <summary>Completes when startup recovery is done (never faults).</summary>
    public Task Recovered => _recovered;

    /// <summary>Completes when recovery and retention are done (never faults).</summary>
    public Task Completed => _completed;

    /// <summary>Raised (any thread) with a message when a step fails.</summary>
    public event Action<string>? Problem;

    /// <summary>Starts recovery + retention in the background. Call once at app start.</summary>
    public void Start()
    {
        _recovered = Task.Run(Recover);
        _completed = _recovered.ContinueWith(_ => ApplyRetention(), TaskScheduler.Default);
    }

    private void Recover()
    {
        try
        {
            var result = _store.RecoverIncomplete(finalizeAudio: FinalizePart);
            foreach (var error in result.Errors)
                Console.WriteLine($"[VoiceType.Uno] History recovery: {error.Path}: {error.Message}");
            if (result.Recovered.Count > 0 || result.MarkedIncomplete.Count > 0)
                Console.WriteLine($"[VoiceType.Uno] History recovery: {result.Recovered.Count} recovered, {result.MarkedIncomplete.Count} incomplete");
        }
        catch (Exception ex)
        {
            Problem?.Invoke($"History recovery failed: {ex.Message}");
        }
    }

    private void ApplyRetention()
    {
        try
        {
            var applied = RetentionPolicy.Apply(_store, _settings.Current.History.Clone(), DateTimeOffset.Now);
            if (applied.Count > 0)
                Console.WriteLine($"[VoiceType.Uno] History retention: audio removed from {applied.Count} recording(s)");
        }
        catch (Exception ex)
        {
            Problem?.Invoke($"History cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Repairs a leftover <c>.part</c> (closes the Ogg stream / patches WAV sizes) and
    /// renames it to its final name. A part without usable headers is deleted.
    /// </summary>
    internal static bool FinalizePart(string partPath, string finalPath)
    {
        try
        {
            RecordingAudioRecovery.Recover(partPath);
            return File.Exists(finalPath);
        }
        catch (InvalidDataException)
        {
            try { File.Delete(partPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
