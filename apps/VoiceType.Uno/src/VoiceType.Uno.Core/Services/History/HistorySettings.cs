using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

[JsonConverter(typeof(LenientEnumConverter<AudioQuality>))]
public enum AudioQuality
{
    /// <summary>
    /// Ogg Opus 48 kbps (~19.5 MB/h). Phase-0 measurement: +0.23 pp WER on re-recognition
    /// (Parakeet INT4, CV17 en+ru) — the lowest bitrate below the 0.3 pp threshold.
    /// </summary>
    Standard = 0,
    /// <summary>WAV PCM16 (~115 MB/h): re-recognition sees exactly the live samples.</summary>
    High,
    /// <summary>Ogg Opus 24 kbps (~10 MB/h, +0.8 pp WER) for phones short on storage.</summary>
    Compact,
}

/// <summary>The "History" section of <c>settings.json</c>.</summary>
public sealed class HistorySettings
{
    /// <summary>Retention choices offered in Settings (0 = forever).</summary>
    public static readonly IReadOnlyList<int> RetentionDayChoices = [7, 30, 90, 180, 365, 0];

    /// <summary>Save recordings to History at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delete audio older than this many days; 0 = keep forever. Text is never auto-deleted.</summary>
    public int AudioRetentionDays { get; set; } = 30;

    /// <summary>Cap for all stored audio; oldest audio goes first. 0 = unlimited.</summary>
    public int MaxAudioStorageMb { get; set; } = 2048;

    /// <summary>Ask before deleting a recording/version/chat/audio ("Don't ask again" clears it).</summary>
    public bool ConfirmDelete { get; set; } = true;

    /// <summary>Also save sessions recorded while Inject text was on.</summary>
    public bool SaveInjectDictations { get; set; } = true;

    public AudioQuality AudioQuality { get; set; } = AudioQuality.Standard;

    /// <summary>Let queued jobs run next to a live recording (Android shows a CPU/battery warning).</summary>
    public bool AllowJobsDuringRecording { get; set; } = true;

    [JsonIgnore]
    public bool KeepAudioForever => AudioRetentionDays <= 0;

    [JsonIgnore]
    public long MaxAudioStorageBytes => MaxAudioStorageMb <= 0 ? 0 : MaxAudioStorageMb * 1024L * 1024L;

    /// <summary>Whether a finished session should be written to History.</summary>
    public bool ShouldSaveRecording(bool injectTextWasOn) =>
        Enabled && (!injectTextWasOn || SaveInjectDictations);

    public HistorySettings Clone() => (HistorySettings)MemberwiseClone();
}
