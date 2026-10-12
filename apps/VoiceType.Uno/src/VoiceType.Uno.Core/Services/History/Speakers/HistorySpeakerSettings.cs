using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

/// <summary>Background speaker detection after a recording stops (design §11.5, <c>Speakers.AfterStop</c>).</summary>
[JsonConverter(typeof(LenientEnumConverter<SpeakerAutoDetect>))]
public enum SpeakerAutoDetect
{
    /// <summary>Only on demand (Detect speakers, Reprocess).</summary>
    Off = 0,

    /// <summary>Best quality offline run after Stop, when idle.</summary>
    Best,
}

/// <summary>
/// STUB (agent X2): the minimal <c>HistorySettings.Speakers</c> bag the settings UI binds to.
/// Agent X1 defines the real one (same property names: <c>AfterStop</c>, <c>ThreadsHint</c>);
/// X1's definition replaces this file at merge.
/// </summary>
public sealed class HistorySpeakerSettings
{
    public SpeakerAutoDetect AfterStop { get; set; } = SpeakerAutoDetect.Best;

    /// <summary>CPU threads for the diarization model; 0 = automatic.</summary>
    public int ThreadsHint { get; set; }

    public HistorySpeakerSettings Clone() => (HistorySpeakerSettings)MemberwiseClone();
}
