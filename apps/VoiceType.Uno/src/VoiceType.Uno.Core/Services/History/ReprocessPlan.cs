namespace VoiceType.Uno.History;

/// <summary>Pure helpers of the Reprocess / Re-recognize sheet: "used for vN" marks and the time estimate.</summary>
public static class ReprocessPlan
{
    /// <summary>
    /// "Used for v1, v3" for one recording, "Used in 2 of 3" for several; null when the model
    /// folder made none of their versions.
    /// </summary>
    public static string? UsedText(string modelFolder, IReadOnlyList<RecordingManifest> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        if (recordings.Count == 0)
            return null;

        static bool Uses(TranscriptVersion v, string folder) =>
            v.Kind != VersionKinds.Edited && string.Equals(v.Model?.Folder, folder, StringComparison.OrdinalIgnoreCase);

        if (recordings.Count == 1)
        {
            var ids = recordings[0].Versions.Where(v => Uses(v, modelFolder)).Select(v => v.Id).ToList();
            return ids.Count == 0 ? null : "Used for " + string.Join(", ", ids);
        }

        var count = recordings.Count(r => r.Versions.Any(v => Uses(v, modelFolder)));
        return count == 0 ? null : $"Used in {count} of {recordings.Count}";
    }

    /// <summary>
    /// Real-time factor measured on this device by earlier re-recognitions with the model
    /// (median of <c>stats.rtf</c>), or null when it never ran here.
    /// </summary>
    public static double? MeasuredRealTimeFactor(string modelFolder, IEnumerable<RecordingManifest> recordings)
    {
        var values = recordings
            .SelectMany(r => r.Versions)
            .Where(v => v.Kind == VersionKinds.Rerecognized
                        && string.Equals(v.Model?.Folder, modelFolder, StringComparison.OrdinalIgnoreCase)
                        && v.Stats?.Rtf is > 0)
            .Select(v => v.Stats!.Rtf!.Value)
            .Order()
            .ToList();
        return values.Count == 0 ? null : values[values.Count / 2];
    }

    /// <summary>
    /// Processing time for <paramref name="recordings"/>: measured RTF when known, else the
    /// catalog RTF (measured on a desktop) scaled by <see cref="JobDisplay.StreamingOverhead"/> and by
    /// <paramref name="deviceSlowdown"/> — how much slower this device is than the catalog machine.
    /// </summary>
    public static TimeSpan Estimate(IEnumerable<RecordingManifest> recordings, double? measuredRtf, double? catalogRtf, double deviceSlowdown = 1.0)
    {
        var seconds = recordings.Sum(r => r.Audio is { RemovedAt: null } audio ? audio.DurationSec : 0);
        if (measuredRtf is > 0)
            return TimeSpan.FromSeconds(seconds * measuredRtf.Value);

        var baseline = JobDisplay.EstimateProcessing(seconds, catalogRtf);
        return catalogRtf is > 0 && deviceSlowdown > 0
            ? TimeSpan.FromSeconds(baseline.TotalSeconds * deviceSlowdown)
            : baseline;
    }

    /// <summary>
    /// Slowdown assumed for a phone before any re-recognition ran on it: measured on a Kirin 980 against the
    /// desktop catalog RTFs (Nemotron INT4 about 3.2x, Parakeet INT4 about 4.7x, including the streaming overhead).
    /// </summary>
    public const double PhoneSlowdown = 4.0;

    /// <summary>
    /// How much slower this device is than the machine the catalog RTFs were measured on: the median of
    /// <c>measured RTF / (catalog RTF × streaming overhead)</c> over the re-recognitions already done here with
    /// any model, or <paramref name="fallback"/> when none ran yet (1 on a desktop, <see cref="PhoneSlowdown"/> on a phone).
    /// </summary>
    public static double DeviceSlowdown(IEnumerable<RecordingManifest> recordings, Func<string, double?> catalogRtfOf, double fallback)
    {
        var ratios = new List<double>();
        foreach (var version in recordings.SelectMany(r => r.Versions))
        {
            if (version.Kind != VersionKinds.Rerecognized || version.Stats?.Rtf is not > 0 || version.Model?.Folder is not { Length: > 0 } folder)
                continue;
            var catalog = catalogRtfOf(folder);
            if (catalog is not > 0)
                continue;
            ratios.Add(version.Stats.Rtf.Value / (catalog.Value * JobDisplay.StreamingOverhead));
        }

        if (ratios.Count == 0)
            return fallback;
        ratios.Sort();
        return Math.Max(1.0, ratios[ratios.Count / 2]);
    }

    /// <summary>
    /// Whether the recognition language can be chosen for a model family. Parakeet TDT v3 detects the language
    /// itself and has no language input (English speech came out as Russian words when forced to English).
    /// </summary>
    public static bool SupportsLanguageChoice(string? family) =>
        !string.Equals(family, "ParakeetTdt", StringComparison.OrdinalIgnoreCase);

    /// <summary>The id the next version of <paramref name="manifest"/> will get (<c>v3</c>).</summary>
    public static string NextVersionId(RecordingManifest manifest) =>
        RecordingStore.NextId(manifest.Versions.Select(v => v.Id), "v");
}
