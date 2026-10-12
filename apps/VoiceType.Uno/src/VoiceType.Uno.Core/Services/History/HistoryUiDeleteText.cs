namespace VoiceType.Uno.History;

/// <summary>Title and body of a delete confirmation.</summary>
public sealed record HistoryUiConfirmation(string Title, string Body);

/// <summary>Texts of the delete confirmations and the Undo snackbar (design §4.7).</summary>
public static class HistoryUiDeleteText
{
    public static HistoryUiConfirmation Recordings(int count, long audioBytes, int chatCount)
    {
        var title = count == 1 ? "Delete this recording?" : $"Delete {count} recordings?";
        var audio = audioBytes > 0 ? $"Audio ({HistoryUiFormat.Bytes(audioBytes)}), transcripts" : "Transcripts";
        var chats = chatCount > 0
            ? " Chats keep their messages but lose " + (count == 1 ? "this source." : "these sources.")
            : "";
        return new HistoryUiConfirmation(title,
            $"{audio}, versions and translations are deleted from this device.{chats} This can't be undone.");
    }

    /// <param name="translationLanguages">Languages of the translations made from the version.</param>
    /// <param name="chatCount">Chats that read exactly this version.</param>
    public static HistoryUiConfirmation Version(
        string versionId, string currentVersionId, IReadOnlyCollection<string> translationLanguages, int chatCount)
    {
        ArgumentNullException.ThrowIfNull(translationLanguages);
        var parts = new List<string> { $"Transcript {versionId} is deleted; the other versions stay." };
        if (translationLanguages.Count > 0)
            parts.Add((translationLanguages.Count == 1 ? "Its translation " : "Its translations ")
                      + string.Join(", ", translationLanguages.Select(l => l.ToUpperInvariant()))
                      + " " + (translationLanguages.Count == 1 ? "is" : "are") + " deleted too.");
        if (chatCount > 0)
            parts.Add($"{HistoryUiFormat.Count(chatCount, "chat")} {(chatCount == 1 ? "reads" : "read")} this version; "
                      + $"{(chatCount == 1 ? "it switches" : "they switch")} to {currentVersionId} (current).");
        return new HistoryUiConfirmation($"Delete version {versionId}?", string.Join(" ", parts));
    }

    public static HistoryUiConfirmation AllHistory(int count, long totalBytes) => new(
        "Delete all history?",
        $"{HistoryUiFormat.Count(count, "recording")} ({HistoryUiFormat.Bytes(totalBytes)}) with their audio, versions "
        + "and translations are deleted from this device. Chats keep their messages. This can't be undone.");

    public static string Snackbar(int recordings) =>
        recordings == 1 ? "Recording deleted" : $"{recordings} recordings deleted";
}
