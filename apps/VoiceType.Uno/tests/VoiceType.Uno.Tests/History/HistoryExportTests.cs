using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryExportTests
{
    private static TranscriptDocument Transcript() =>
        HistoryFixtures.Transcript((0, 4.5, "Hello there."), (65.25, 70, "Second line."), (3725.5, 3726, "Hour mark."));

    private static TranslationDocument Translation() => new()
    {
        Segments =
        [
            new TranslationSegment { Index = 0, Text = "Привет." },
            new TranslationSegment { Index = 2, Text = "Час." },
        ],
    };

    [Fact]
    public void ToText_ShouldWriteTimestampPerSegment()
    {
        const string expected =
            "[00:00:00] Hello there.\n" +
            "[00:01:05] Second line.\n" +
            "[01:02:05] Hour mark.\n";

        Assert.Equal(expected, TranscriptExport.ToText(Transcript()));
    }

    [Fact]
    public void ToText_ShouldInterleaveTranslation()
    {
        const string expected =
            "[00:00:00] Hello there.\n" +
            "           Привет.\n" +
            "\n" +
            "[00:01:05] Second line.\n" +
            "\n" +
            "[01:02:05] Hour mark.\n" +
            "           Час.\n";

        Assert.Equal(expected, TranscriptExport.ToText(Transcript(), Translation()));
    }

    [Fact]
    public void ToSrt_ShouldWriteNumberedCues()
    {
        const string expected =
            "1\n00:00:00,000 --> 00:00:04,500\nHello there.\n\n" +
            "2\n00:01:05,250 --> 00:01:10,000\nSecond line.\n\n" +
            "3\n01:02:05,500 --> 01:02:06,000\nHour mark.\n\n";

        Assert.Equal(expected, TranscriptExport.ToSrt(Transcript()));
    }

    [Fact]
    public void ToSrt_ShouldAddTranslationLineAndSkipEmptySegments()
    {
        var doc = HistoryFixtures.Transcript((0, 1, "Hi."), (1, 1, "  "), (2, 2, "Bye."));
        var translation = new TranslationDocument { Segments = [new TranslationSegment { Index = 0, Text = "Привет." }] };

        const string expected =
            "1\n00:00:00,000 --> 00:00:01,000\nHi.\nПривет.\n\n" +
            "2\n00:00:02,000 --> 00:00:03,000\nBye.\n\n";

        Assert.Equal(expected, TranscriptExport.ToSrt(doc, translation));
    }

    [Fact]
    public void ToLrc_ShouldWriteHeaderLinesAndClearingStamps()
    {
        const string expected =
            "[ti:Weekly sync]\n" +
            "[length:62:06]\n" +
            "[00:00.00]Hello there.\n" +
            "[00:04.50]\n" +
            "[01:05.25]Second line.\n" +
            "[01:10.00]\n" +
            "[62:05.50]Hour mark.\n" +
            "[62:06.00]\n";

        Assert.Equal(expected, TranscriptExport.ToLrc(Transcript(), "Weekly sync", 3726.4));
    }

    [Fact]
    public void ToLrc_ShouldNotClearBetweenAdjacentSegments()
    {
        var doc = HistoryFixtures.Transcript((0, 2, "One."), (2.5, 4, "Two."));

        Assert.Equal("[00:00.00]One.\n[00:02.50]Two.\n[00:04.00]\n", TranscriptExport.ToLrc(doc));
    }

    [Theory]
    [InlineData(0, "00:00", "00:00:00")]
    [InlineData(59.99, "00:59", "00:00:59")]
    [InlineData(200, "03:20", "00:03:20")]
    [InlineData(3725.5, "1:02:05", "01:02:05")]
    [InlineData(-3, "00:00", "00:00:00")]
    public void Formatters_ShouldFormatTimes(double seconds, string shortForm, string clock)
    {
        Assert.Equal(shortForm, TranscriptExport.FormatShort(seconds));
        Assert.Equal(clock, TranscriptExport.FormatClock(seconds));
    }

    [Fact]
    public void ChatToMarkdown_ShouldRenderSourcesMessagesAndCitations()
    {
        var recordings = new Dictionary<string, RecordingManifest>
        {
            ["aaaaaa"] = new()
            {
                Id = "aaaaaa", Title = "Weekly sync", StartedAt = HistoryFixtures.T0,
                Versions = [new TranscriptVersion { Id = "v2", Kind = VersionKinds.Rerecognized, Model = new ModelInfo { Title = "Parakeet INT4" } }],
            },
        };
        var chat = new ChatRecord
        {
            Id = "c1", Title = "Budget questions",
            CreatedAt = HistoryFixtures.T0, UpdatedAt = HistoryFixtures.T0.AddMinutes(10),
            Sources = [new ChatSource("aaaaaa", "v2", HistoryFixtures.T0), new ChatSource("bbbbbb", "v1", HistoryFixtures.T0)],
            Messages =
            [
                new ChatMessage { Role = ChatRoles.System, Text = "hidden", CreatedAt = HistoryFixtures.T0 },
                new ChatMessage { Role = ChatRoles.User, Text = "When is the meeting?", CreatedAt = HistoryFixtures.T0.AddMinutes(3) },
                new ChatMessage
                {
                    Role = ChatRoles.Assistant, Text = "Monday at ten.", CreatedAt = HistoryFixtures.T0.AddMinutes(4),
                    Citations =
                    [
                        new ChatCitation { RecordingId = "aaaaaa", StartSec = 200 },
                        new ChatCitation { RecordingId = "bbbbbb", StartSec = 3725 },
                    ],
                },
            ],
        };

        const string expected =
            "# Budget questions\n\n" +
            "Created 2026-10-12 14:02 · updated 2026-10-12 14:12\n\n" +
            "## Sources\n\n" +
            "- **Weekly sync** — recorded 2026-10-12 14:02, version v2 (rerecognized), model Parakeet INT4\n" +
            "- *Recording bbbbbb (deleted)* — version v1\n\n" +
            "## Conversation\n" +
            "\n### You · 14:05\n\nWhen is the meeting?\n" +
            "\n### Gemma · 14:06\n\nMonday at ten.\n" +
            "\nSources: [Weekly sync 03:20], [Recording bbbbbb 1:02:05]\n";

        Assert.Equal(expected, ChatExport.ToMarkdown(chat, recordings));
    }

    [Theory]
    [InlineData("Weekly: sync / Q4?", "Weekly_ sync _ Q4_")]
    [InlineData("  trailing dots... ", "trailing dots")]
    [InlineData("***", "___")]
    [InlineData("", "recording")]
    public void SanitizeFileName_ShouldReplaceInvalidCharacters(string input, string expected) =>
        Assert.Equal(expected, ExportFiles.SanitizeFileName(input));

    [Fact]
    public void SuggestBaseName_ShouldCombineDateAndTitle()
    {
        var manifest = new RecordingManifest { StartedAt = HistoryFixtures.T0, Title = "Weekly sync" };

        Assert.Equal("2026-10-12 14-02 Weekly sync", ExportFiles.SuggestBaseName(manifest));
    }

    [Fact]
    public void WriteSubtitles_ShouldWriteSrtAndLrcSideBySide()
    {
        using var dir = new TempDirectory();

        var paths = ExportFiles.WriteSubtitles(dir.Path, "meeting", Transcript(), "Weekly sync");

        Assert.Equal(["meeting.srt", "meeting.lrc"], paths.Select(Path.GetFileName));
        Assert.Equal(TranscriptExport.ToSrt(Transcript()), File.ReadAllText(paths[0]));
        var bytes = File.ReadAllBytes(paths[1]);
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
    }
}
