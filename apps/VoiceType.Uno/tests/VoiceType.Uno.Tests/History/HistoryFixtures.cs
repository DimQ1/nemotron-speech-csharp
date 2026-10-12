using VoiceType.Uno.History;

namespace VoiceType.Uno.Tests.History;

internal static class HistoryFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 12, 14, 2, 11, TimeSpan.Zero);

    /// <summary>One segment per (start, end, text); words estimated evenly by length.</summary>
    public static TranscriptDocument Transcript(params (double Start, double End, string Text)[] segments)
    {
        var doc = new TranscriptDocument();
        for (var i = 0; i < segments.Length; i++)
        {
            var (start, end, text) = segments[i];
            doc.Segments.Add(new TranscriptSegment
            {
                Index = i,
                Start = start,
                End = end,
                Text = text,
                Words = TranscriptTimings.Distribute(TextTokens.SplitWords(text), start, end),
            });
        }
        doc.Text = TranscriptDocument.JoinSegments(doc.Segments);
        return doc;
    }

    public static TranscriptVersion LiveSpec(string modelFolder = "parakeet-int4", string language = "en") => new()
    {
        Kind = VersionKinds.Live,
        Model = new ModelInfo { Folder = modelFolder, Title = "Parakeet INT4", CatalogRepoId = "repo/" + modelFolder },
        Language = new LanguageInfo { Requested = "auto", Detected = language },
        Settings = JsonValues.Settings(("useVad", true), ("repetitionPenalty", 1.1), ("executionProvider", "cpu")),
        App = new AppInfo { Name = "VoiceType.Uno", Version = "1.0", Platform = "win-x64" },
    };
}
