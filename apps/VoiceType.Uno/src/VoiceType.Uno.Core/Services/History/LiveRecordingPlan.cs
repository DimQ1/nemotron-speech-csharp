using System.Text.Json;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.History;

/// <summary>
/// What a live recording session does with History, decided once at session start
/// (design §5.7: the decision follows the modes at the START of the session, so a long
/// meeting is not lost when Inject text is switched on midway).
/// </summary>
/// <param name="Save">Write the session to History at all.</param>
/// <param name="Mode">Modes at session start, stored in the manifest.</param>
/// <param name="AudioFormatSpec"><c>SpeechLib.AudioStorage.RecordingAudioFormat.Parse</c> spec.</param>
public sealed record LiveRecordingPlan(bool Save, RecordingMode Mode, string AudioFormatSpec, AudioQuality Quality)
{
    public static LiveRecordingPlan Decide(HistorySettings settings, bool injectText, bool translation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new LiveRecordingPlan(
            settings.ShouldSaveRecording(injectText),
            new RecordingMode { InjectText = injectText, Translation = translation },
            HistoryAudioFormats.Spec(settings.AudioQuality),
            settings.AudioQuality);
    }
}

/// <summary>Storage format per <see cref="AudioQuality"/> (decisions: Opus 48 / WAV / Opus 24).</summary>
public static class HistoryAudioFormats
{
    public static string Spec(AudioQuality quality) => quality switch
    {
        AudioQuality.High => "wav",
        AudioQuality.Compact => "opus:24",
        _ => "opus:48",
    };

    /// <summary>Codec name stored in <see cref="AudioInfo.Codec"/>.</summary>
    public static string Codec(AudioQuality quality) => quality == AudioQuality.High ? "pcm16" : "opus";

    /// <summary>Bitrate in bit/s stored in <see cref="AudioInfo.Bitrate"/>; null for WAV.</summary>
    public static int? BitrateBps(AudioQuality quality) => quality switch
    {
        AudioQuality.High => null,
        AudioQuality.Compact => 24_000,
        _ => 48_000,
    };

    public static string FileName(AudioQuality quality) => quality == AudioQuality.High ? "audio.wav" : "audio.ogg";
}

/// <summary>Recognizer facts captured when the session starts (for the version metadata).</summary>
/// <param name="ModelPath">Loaded model folder.</param>
/// <param name="Family">Recognizer family (ParakeetTdt, Nemotron, Qwen3, …).</param>
/// <param name="Settings">Recognizer-specific values that change the result (chunk, contexts, EOU…).</param>
public sealed record LiveRecognizerInfo(
    string? ModelPath,
    string? Family,
    int SampleRate,
    bool UtteranceCommits,
    double CommitLagSeconds,
    IReadOnlyList<KeyValuePair<string, object?>> Settings);

/// <summary>
/// Builds the manifest pieces of a live recording: model description, version spec
/// (kind <c>live</c>, design §9.2), audio info with gaps, and the decision whether the
/// finished session is kept and whether the user edited it during recording.
/// </summary>
public static class LiveRecordingMetadata
{
    public const string AppName = "VoiceType.Uno";

    /// <summary>
    /// Describes a model folder. Catalog models get repo id, title, precision and variant
    /// from <see cref="ModelCatalog"/>; others fall back to <paramref name="fallbackRepoId"/> /
    /// <paramref name="fallbackTitle"/> (the app's default model is not in the catalog) and a
    /// precision guessed from the folder name.
    /// </summary>
    public static ModelInfo DescribeModel(
        string? modelPath, string? family, string? fallbackRepoId = null, string? fallbackTitle = null)
    {
        var folder = string.IsNullOrWhiteSpace(modelPath)
            ? null
            : Path.GetFileName(modelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var descriptor = string.IsNullOrEmpty(folder) ? null : ModelCatalog.FindBySubfolder(folder);
        if (descriptor is not null)
        {
            return new ModelInfo
            {
                CatalogRepoId = descriptor.RepoId,
                Folder = folder,
                Title = descriptor.Title,
                Precision = descriptor.Precision.ToString(),
                Variant = descriptor.Variant,
                Family = family,
            };
        }

        return new ModelInfo
        {
            CatalogRepoId = fallbackRepoId,
            Folder = folder,
            Title = fallbackTitle ?? (folder is null ? null : KnownModels.Describe(folder)),
            Precision = GuessPrecision(folder),
            Family = family,
        };
    }

    /// <summary>
    /// Recognizer family of a model folder, detected the same way
    /// <c>RecognizerFactory</c> picks the provider: VibeVoice, Qwen3Streaming, Qwen3,
    /// ParakeetTdt, else Nemotron (ORT GenAI).
    /// </summary>
    public static string? DetectFamily(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            return null;
        if (SpeechLib.ModelFormats.VibeVoiceModelDetector.IsVibeVoiceAsrModel(modelPath)) return "VibeVoice";
        if (SpeechLib.ModelFormats.Qwen3ModelDetector.IsQwen3AsrStreamingModel(modelPath)) return "Qwen3Streaming";
        if (SpeechLib.ModelFormats.Qwen3ModelDetector.IsQwen3AsrModel(modelPath)) return "Qwen3";
        if (SpeechLib.ModelFormats.ParakeetModelDetector.IsParakeetTdtModel(modelPath)) return "ParakeetTdt";
        return "Nemotron";
    }

    /// <summary>Int4 / Int8 / Fp32 / Fp16 from a folder name, or null.</summary>
    public static string? GuessPrecision(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;
        var f = folder.ToLowerInvariant();
        if (f.Contains("int4", StringComparison.Ordinal) || f.Contains("q4", StringComparison.Ordinal)) return "Int4";
        if (f.Contains("int8", StringComparison.Ordinal) || f.Contains("q8", StringComparison.Ordinal)) return "Int8";
        if (f.Contains("fp16", StringComparison.Ordinal)) return "Fp16";
        if (f.Contains("fp32", StringComparison.Ordinal)) return "Fp32";
        return null;
    }

    /// <summary>
    /// The spec for <see cref="RecordingStore.AddVersion"/>: kind live, model, language
    /// (requested + detected), every result-affecting setting, app info.
    /// </summary>
    public static TranscriptVersion BuildLiveVersion(
        AppSettings settings,
        LiveRecognizerInfo recognizer,
        ModelInfo model,
        string? detectedLanguage,
        AppInfo app,
        double? elapsedSec = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recognizer);

        var values = new List<(string, object?)>
        {
            ("useVad", settings.UseVad),
            ("repetitionPenalty", settings.RepetitionPenalty),
            ("executionProvider", settings.ExecutionProvider),
            ("audioSource", settings.AudioSource),
            ("sampleRate", recognizer.SampleRate),
        };
        foreach (var (key, value) in recognizer.Settings)
        {
            if (!values.Exists(v => v.Item1 == key))
                values.Add((key, value));
        }

        return new TranscriptVersion
        {
            Kind = VersionKinds.Live,
            Model = model,
            Language = new LanguageInfo
            {
                Requested = string.IsNullOrWhiteSpace(settings.Language) ? "auto" : settings.Language,
                Detected = detectedLanguage,
            },
            Settings = JsonValues.Settings(values.ToArray()),
            App = app,
            Stats = new VersionStats { ElapsedSec = elapsedSec },
        };
    }

    /// <summary>Audio info for the manifest; gaps are (file position, muted length) in samples.</summary>
    public static AudioInfo BuildAudioInfo(
        string fileName,
        AudioQuality quality,
        int sampleRate,
        long samples,
        long bytes,
        string? source,
        IEnumerable<(long AtSample, long MutedSamples)> gaps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return new AudioInfo
        {
            File = fileName,
            Codec = HistoryAudioFormats.Codec(quality),
            SampleRate = sampleRate,
            Channels = 1,
            Bitrate = HistoryAudioFormats.BitrateBps(quality),
            DurationSec = Math.Round((double)samples / sampleRate, 3),
            Bytes = bytes,
            Source = source,
            Gaps = gaps
                .Where(g => g.MutedSamples > 0)
                .Select(g => new AudioGap
                {
                    AtSec = Math.Round((double)g.AtSample / sampleRate, 3),
                    WallSec = Math.Round((double)g.MutedSamples / sampleRate, 3),
                    Reason = "muted",
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Keeps a finished session when it has any text (recognized or typed) or at least
    /// <paramref name="minAudioSeconds"/> of audio (worth re-recognizing later); an
    /// accidental tap on Record leaves nothing behind.
    /// </summary>
    public static bool ShouldKeep(string? recognizedText, string? editedText, double audioSeconds, double minAudioSeconds = 3.0) =>
        !string.IsNullOrWhiteSpace(recognizedText)
        || !string.IsNullOrWhiteSpace(editedText)
        || audioSeconds >= minAudioSeconds;

    /// <summary>
    /// The text the user left for this session when it differs from what was recognized,
    /// else null. <paramref name="fieldAtStart"/> is the transcript field when the session
    /// began (text kept from earlier sessions); only what follows it belongs to this
    /// session. <paramref name="userTouched"/>: manual input was on or the field was
    /// cleared during the session — without it, differences are only formatting.
    /// </summary>
    public static string? DetectLiveEdit(string? fieldAtStart, string? fieldAtEnd, string? recognizedText, bool userTouched)
    {
        if (!userTouched)
            return null;

        var start = fieldAtStart ?? "";
        var end = fieldAtEnd ?? "";
        var session = start.Length > 0 && end.StartsWith(start, StringComparison.Ordinal)
            ? end[start.Length..]
            : end;
        var edited = LiveSegmentTimer.CleanText(session);
        if (edited.Length == 0)
            return null;

        return string.Equals(edited, LiveSegmentTimer.CleanText(recognizedText), StringComparison.Ordinal)
            ? null
            : edited;
    }

    /// <summary><c>"edit": { "source": "live" }</c> extension of a version edited during recording.</summary>
    public static Dictionary<string, JsonElement> LiveEditExtension()
    {
        using var doc = JsonDocument.Parse("{\"source\":\"live\"}");
        return new Dictionary<string, JsonElement> { ["edit"] = doc.RootElement.Clone() };
    }

    /// <summary>Invariant "1.2.3" form of a version, without the source-revision suffix.</summary>
    public static string? CleanVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return null;
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
    }
}
