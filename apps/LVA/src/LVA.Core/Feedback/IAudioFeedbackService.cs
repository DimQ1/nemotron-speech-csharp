namespace LVA.Core.Feedback;

/// <summary>Sound cues confirming state changes before any text/speech (TZ §14.11).</summary>
public enum SoundCue
{
    ListeningStarted,
    Activated,
    TaskStarted,
    TaskCompleted,
    TaskFailed,
    Poked,
    Petting
}

/// <summary>
/// Audio feedback service: tiny confirmation sounds (<50 ms from event).
/// Soundpack-based (WAV/OGG + manifest), replaceable via configuration.
/// </summary>
public interface IAudioFeedbackService
{
    bool Enabled { get; set; }
    float Volume { get; set; }

    void Play(SoundCue cue);
}

/// <summary>Soundpack manifest entry.</summary>
public sealed record SoundPackEntry(SoundCue Cue, string File, float DefaultVolume = 1.0f);

/// <summary>
/// Default soundpack player. Audio playback is delegated to a platform player
/// (MAUI: Plugin.Maui.Audio AudioPlayer; tests: no-op).
/// </summary>
public sealed class SoundPackAudioFeedbackService : IAudioFeedbackService
{
    private readonly Dictionary<SoundCue, string> _cueFiles = new();
    private readonly Func<string, IDisposable?> _playerFactory;

    public bool Enabled { get; set; } = true;
    public float Volume { get; set; } = 0.6f;

    /// <param name="soundPackDir">Directory with WAV/OGG files + manifest.</param>
    /// <param name="playerFactory">Creates a player for a file path (null → silent).</param>
    public SoundPackAudioFeedbackService(string? soundPackDir = null, Func<string, IDisposable?>? playerFactory = null)
    {
        _playerFactory = playerFactory ?? (_ => null);
        if (soundPackDir is not null)
            LoadPack(soundPackDir);
    }

    private void LoadPack(string dir)
    {
        // Convention: <cue>.wav|ogg in the pack directory.
        foreach (SoundCue cue in Enum.GetValues<SoundCue>())
        {
            var name = cue.ToString().ToLowerInvariant();
            foreach (var ext in new[] { ".wav", ".ogg", ".mp3" })
            {
                var path = Path.Combine(dir, name + ext);
                if (File.Exists(path))
                {
                    _cueFiles[cue] = path;
                    break;
                }
            }
        }
    }

    public void Play(SoundCue cue)
    {
        if (!Enabled || !_cueFiles.TryGetValue(cue, out var file))
            return;
        _playerFactory(file); // fire-and-forget; player owns its lifecycle
    }
}
