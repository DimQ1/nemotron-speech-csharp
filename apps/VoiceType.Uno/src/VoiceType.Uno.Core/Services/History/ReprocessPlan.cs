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
    /// catalog RTF scaled by <see cref="JobDisplay.StreamingOverhead"/>.
    /// </summary>
    public static TimeSpan Estimate(IEnumerable<RecordingManifest> recordings, double? measuredRtf, double? catalogRtf)
    {
        var seconds = recordings.Sum(r => r.Audio is { RemovedAt: null } audio ? audio.DurationSec : 0);
        return measuredRtf is > 0
            ? TimeSpan.FromSeconds(seconds * measuredRtf.Value)
            : JobDisplay.EstimateProcessing(seconds, catalogRtf);
    }

    /// <summary>The id the next version of <paramref name="manifest"/> will get (<c>v3</c>).</summary>
    public static string NextVersionId(RecordingManifest manifest) =>
        RecordingStore.NextId(manifest.Versions.Select(v => v.Id), "v");
}
