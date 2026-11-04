using VoiceType.Uno.Localization;

namespace VoiceType.Uno.History;

/// <summary>Title and body of a delete confirmation.</summary>
public sealed record HistoryUiConfirmation(string Title, string Body);

/// <summary>Texts of the delete confirmations and the Undo snackbar (design §4.7).</summary>
public static class HistoryUiDeleteText
{
    public static HistoryUiConfirmation Recordings(int count, long audioBytes, int chatCount)
    {
        var title = count == 1 ? Loc.T("History.Delete.Recordings.TitleThis") : Loc.Plural("History.Delete.Recordings.Title", count);
        var sentences = new List<string>
        {
            audioBytes > 0
                ? Loc.F("History.Delete.Recordings.BodyAudio", HistoryUiFormat.Bytes(audioBytes))
                : Loc.T("History.Delete.Recordings.BodyText"),
        };
        if (chatCount > 0)
            sentences.Add(Loc.T(count == 1 ? "History.Delete.Recordings.ChatsThis" : "History.Delete.Recordings.ChatsThese"));
        sentences.Add(Loc.T("History.Delete.CannotUndo"));
        return new HistoryUiConfirmation(title, string.Join(" ", sentences));
    }

    /// <param name="translationLanguages">Languages of the translations made from the version.</param>
    /// <param name="chatCount">Chats that read exactly this version.</param>
    public static HistoryUiConfirmation Version(
        string versionId, string currentVersionId, IReadOnlyCollection<string> translationLanguages, int chatCount)
    {
        ArgumentNullException.ThrowIfNull(translationLanguages);
        var parts = new List<string> { Loc.F("History.Delete.Version.Body", versionId) };
        if (translationLanguages.Count > 0)
            parts.Add(Loc.Plural("History.Delete.Version.Translations", translationLanguages.Count,
                string.Join(", ", translationLanguages.Select(l => l.ToUpperInvariant()))));
        if (chatCount > 0)
            parts.Add(Loc.Plural("History.Delete.Version.Chats", chatCount, currentVersionId));
        return new HistoryUiConfirmation(Loc.F("History.Delete.Version.Title", versionId), string.Join(" ", parts));
    }

    public static HistoryUiConfirmation AllHistory(int count, long totalBytes) => new(
        Loc.T("History.Delete.All.Title"),
        Loc.Plural("History.Delete.All.Body", count, HistoryUiFormat.Bytes(totalBytes)));

    public static string Snackbar(int recordings) =>
        recordings == 1 ? Loc.T("History.Delete.Snackbar.This") : Loc.Plural("History.Delete.Snackbar", recordings);
}
