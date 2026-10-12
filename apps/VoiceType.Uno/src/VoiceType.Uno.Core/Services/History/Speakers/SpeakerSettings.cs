using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

/// <summary><c>Speakers.AutoAfterStop</c>: background speaker detection after a recording stops.</summary>
[JsonConverter(typeof(LenientEnumConverter<SpeakerAutoMode>))]
public enum SpeakerAutoMode
{
    /// <summary>Only on request (the Speakers sheet, Reprocess).</summary>
    Off = 0,
    /// <summary>Best quality: the offline 30 s buffer, as a background job when idle.</summary>
    Best,
}

/// <summary>The <c>Speakers</c> block of the History settings.</summary>
public sealed class SpeakerSettings
{
    /// <summary>Intra-op threads of the diarization model on a desktop.</summary>
    public const int DesktopThreads = 4;

    /// <summary>Intra-op threads on a phone (next to streaming ASR; more are slower there).</summary>
    public const int PhoneThreads = 2;

    /// <summary>
    /// Detect speakers after a recording was saved. Null = the platform default: <see cref="SpeakerAutoMode.Best"/> on a
    /// desktop when the model is installed, <see cref="SpeakerAutoMode.Off"/> on a phone (see <see cref="Effective"/>).
    /// </summary>
    public SpeakerAutoMode? AutoAfterStop { get; set; }

    /// <summary>Model threads; 0 = the platform default (<see cref="DesktopThreads"/> / <see cref="PhoneThreads"/>).</summary>
    public int Threads { get; set; }

    /// <summary>Installed model folder to use; null = the first installed one (int8 before fp32).</summary>
    public string? ModelFolder { get; set; }

    /// <summary>The mode that applies on this platform.</summary>
    public SpeakerAutoMode Effective(bool isPhone, bool modelInstalled) =>
        AutoAfterStop ?? (!isPhone && modelInstalled ? SpeakerAutoMode.Best : SpeakerAutoMode.Off);

    public int EffectiveThreads(bool isPhone) =>
        Threads > 0 ? Threads : isPhone ? PhoneThreads : DesktopThreads;

    public SpeakerSettings Clone() => (SpeakerSettings)MemberwiseClone();
}

/// <summary>What automatic reprocessing does (<c>Reprocess.Profile</c>).</summary>
public sealed class ReprocessProfile
{
    /// <summary>ASR model folder; null = keep the current version's model (the re-recognition step is skipped).</summary>
    public string? AsrModelFolder { get; set; }
    public bool Rerecognize { get; set; } = true;
    public bool Diarize { get; set; } = true;
    public bool Analyze { get; set; } = true;
    public bool MakeCurrent { get; set; } = true;

    public ReprocessProfile Clone() => (ReprocessProfile)MemberwiseClone();
}

/// <summary>The <c>Reprocess</c> block of the History settings.</summary>
public sealed class ReprocessSettings
{
    /// <summary>Queue a Reprocess chain for every new recording.</summary>
    public bool AutoNew { get; set; }

    public ReprocessProfile Profile { get; set; } = new();

    /// <summary>When the chain runs: tonight, on the charger.</summary>
    public JobCondition AutoCondition { get; set; } = new()
    {
        Kind = JobConditionKind.AtTime,
        WindowStart = new TimeOnly(1, 0),
        WindowEnd = new TimeOnly(6, 0),
        RequireCharging = true,
    };

    /// <summary>Recordings shorter than this are not diarized or analysed automatically.</summary>
    public int MinDurationSec { get; set; } = 20;

    public ReprocessSettings Clone()
    {
        var copy = (ReprocessSettings)MemberwiseClone();
        copy.Profile = Profile.Clone();
        copy.AutoCondition = new JobCondition
        {
            Kind = AutoCondition.Kind,
            NotBefore = AutoCondition.NotBefore,
            WindowStart = AutoCondition.WindowStart,
            WindowEnd = AutoCondition.WindowEnd,
            RequireCharging = AutoCondition.RequireCharging,
            RequireIdle = AutoCondition.RequireIdle,
        };
        return copy;
    }
}
