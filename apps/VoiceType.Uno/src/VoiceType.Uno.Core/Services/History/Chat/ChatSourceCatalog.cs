using SpeechLib.Agents;

namespace VoiceType.Uno.History;

/// <summary>
/// The chat's sources as the agent layer reads them before every run: the current chat record (so sources added
/// or removed since the last turn are seen), aliases from the log, transcripts at the chat's own version.
/// </summary>
internal sealed class ChatSourceCatalog(string chatId, ChatStore chats, RecordingStore recordings) : IChatSourceCatalog
{
    private readonly Dictionary<string, IReadOnlyList<TranscriptLine>> _lines = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<ChatSourceDocument> GetSources()
    {
        var chat = chats.Get(chatId);
        if (chat is null)
            return [];

        var registry = ChatSourceEvents.BuildRegistry(chat, out _);
        var result = new List<ChatSourceDocument>();
        foreach (var entry in registry.Entries)
        {
            var source = chat.Sources.Find(s => s.RecordingId == entry.RecordingId);
            if (!entry.Active || source is null)
            {
                result.Add(new ChatSourceDocument(entry.Alias, entry.RecordingId, "", "", null, 0, false, () => []));
                continue;
            }

            var manifest = recordings.Get(source.RecordingId)?.Manifest;
            var recordingId = source.RecordingId;
            var versionId = source.VersionId;
            var lines = LinesOf(recordingId, versionId);
            result.Add(new ChatSourceDocument(
                entry.Alias,
                recordingId,
                versionId,
                manifest?.EffectiveTitle ?? recordingId,
                manifest?.StartedAt,
                manifest?.Audio?.DurationSec is > 0 ? manifest.Audio.DurationSec : lines.Count > 0 ? lines[^1].End : 0,
                true,
                () => lines));
        }
        return result;
    }

    private IReadOnlyList<TranscriptLine> LinesOf(string recordingId, string versionId)
    {
        // A rename or merge changes the names the prompt shows, so the cache key carries the speaker state.
        var key = recordingId + "@" + versionId + "#" + SpeakerLabels.Signature(recordings.Get(recordingId)?.Manifest, versionId);
        lock (_gate)
        {
            if (_lines.TryGetValue(key, out var cached))
                return cached;
            IReadOnlyList<TranscriptLine> lines;
            try
            {
                var doc = recordings.LoadTranscript(recordingId, versionId);
                // Speaker names of the version's current layer ("Anna: text" in the prompt); none without a layer.
                var speakers = SpeakerLabels.ForVersion(recordings, recordingId, versionId);
                lines = doc.Segments
                    .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                    .Select(s => new TranscriptLine(s.Start, s.End > s.Start ? s.End : s.Start, s.Text.Trim(),
                        speakers is not null && speakers.TryGetValue(s.Index, out var name) ? name : null))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException or DirectoryNotFoundException)
            {
                lines = [];
            }
            _lines[key] = lines;
            return lines;
        }
    }
}
