namespace VoiceType.Uno.History;

/// <summary>A recording as the retention policy sees it.</summary>
/// <param name="RecordedAt">End of the recording (or start when unknown).</param>
/// <param name="AudioBytes">Bytes of audio on disk; 0 when there is none.</param>
/// <param name="IsProtected">Being written right now or used by a running job.</param>
public sealed record RetentionCandidate(string RecordingId, DateTimeOffset RecordedAt, long AudioBytes, bool IsProtected = false);

public enum RetentionReason
{
    /// <summary>Older than the retention period.</summary>
    Age,
    /// <summary>Oldest audio removed to get under the storage cap.</summary>
    StorageCap,
}

public sealed record RetentionDecision(string RecordingId, RetentionReason Reason, long AudioBytes);

/// <summary>
/// Decides which recordings lose their audio. Only audio is ever deleted — text,
/// versions, translations and chats stay. Protected recordings are never touched.
/// </summary>
public static class RetentionPolicy
{
    /// <param name="retentionDays">0 or less = keep forever (no age rule).</param>
    /// <param name="maxAudioBytes">0 or less = unlimited (no cap rule).</param>
    public static IReadOnlyList<RetentionDecision> Plan(
        IEnumerable<RetentionCandidate> recordings,
        DateTimeOffset now,
        int retentionDays,
        long maxAudioBytes)
    {
        ArgumentNullException.ThrowIfNull(recordings);

        var withAudio = recordings.Where(r => r.AudioBytes > 0).ToList();
        var decisions = new List<RetentionDecision>();
        var removed = new HashSet<string>(StringComparer.Ordinal);

        if (retentionDays > 0)
        {
            var cutoff = now - TimeSpan.FromDays(retentionDays);
            foreach (var r in withAudio.Where(r => !r.IsProtected && r.RecordedAt < cutoff).OrderBy(r => r.RecordedAt))
            {
                decisions.Add(new RetentionDecision(r.RecordingId, RetentionReason.Age, r.AudioBytes));
                removed.Add(r.RecordingId);
            }
        }

        if (maxAudioBytes > 0)
        {
            var total = withAudio.Where(r => !removed.Contains(r.RecordingId)).Sum(r => r.AudioBytes);
            foreach (var r in withAudio
                         .Where(r => !r.IsProtected && !removed.Contains(r.RecordingId))
                         .OrderBy(r => r.RecordedAt))
            {
                if (total <= maxAudioBytes)
                    break;
                decisions.Add(new RetentionDecision(r.RecordingId, RetentionReason.StorageCap, r.AudioBytes));
                total -= r.AudioBytes;
            }
        }

        return decisions;
    }

    public static IReadOnlyList<RetentionDecision> Plan(
        IEnumerable<RetentionCandidate> recordings, DateTimeOffset now, HistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Plan(recordings, now, settings.AudioRetentionDays, settings.MaxAudioStorageBytes);
    }

    /// <summary>Candidates from the store: audio size measured on disk.</summary>
    public static IReadOnlyList<RetentionCandidate> CollectCandidates(
        IEnumerable<RecordingEntry> recordings, IReadOnlySet<string>? protectedIds = null)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        return recordings
            .Select(e => new RetentionCandidate(
                e.Id,
                e.Manifest.EndedAt ?? e.Manifest.StartedAt,
                e.Manifest.Audio is { RemovedAt: not null } ? 0 : RecordingStore.MeasureAudioBytes(e.FolderPath),
                e.Manifest.State == RecordingStates.Recording || (protectedIds?.Contains(e.Id) ?? false)))
            .ToList();
    }

    /// <summary>
    /// Plans and applies retention on <paramref name="store"/> (app start and after each
    /// save). <paramref name="protectedIds"/>: the recording being written and recordings
    /// with a running job.
    /// </summary>
    public static IReadOnlyList<RetentionDecision> Apply(
        RecordingStore store, HistorySettings settings, DateTimeOffset now, IReadOnlySet<string>? protectedIds = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var candidates = CollectCandidates(store.LoadAll().Recordings, protectedIds);
        var plan = Plan(candidates, now, settings);
        var applied = new List<RetentionDecision>();
        foreach (var decision in plan)
        {
            try
            {
                if (store.DeleteAudio(decision.RecordingId))
                    applied.Add(decision);
            }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException or UnauthorizedAccessException)
            {
                // Skip; the next pass retries.
            }
        }
        return applied;
    }
}
