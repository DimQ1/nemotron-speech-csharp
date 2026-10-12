using System.Text.Json;
using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class ChatUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero);

    private static string? Title(string id) => id switch
    {
        "a" => "Weekly sync",
        "b" => "Supplier call",
        "c" => "A very long recording title that does not fit a chip",
        _ => null,
    };

    private static ChatMessage Msg(string role, string text, int minute = 0, params ChatCitation[] citations) => new()
    {
        Role = role,
        Text = text,
        CreatedAt = new DateTimeOffset(2026, 10, 12, 15, minute, 0, TimeSpan.Zero),
        Citations = [.. citations],
    };

    [Theory]
    [InlineData(599, 800, false)]
    [InlineData(600, 479, false)]
    [InlineData(600, 480, true)]
    [InlineData(1280, 800, true)]
    public void UseSourceChips_ShouldNeed600x480(double w, double h, bool expected) =>
        Assert.Equal(expected, ChatUiLayout.UseSourceChips(w, h));

    [Fact]
    public void IsMasterDetail_ShouldStartAt840() =>
        Assert.True(ChatUiLayout.IsMasterDetail(840) && !ChatUiLayout.IsMasterDetail(839));

    [Fact]
    public void SourcesLine_ShouldNameTheFirstTwoAndCountTheRest()
    {
        Assert.Equal("Weekly sync v2, Supplier call v1, +2",
            ChatUiFormat.SourcesLine([("Weekly sync", "v2"), ("Supplier call", "v1"), ("C", "v1"), ("D", "v3")]));
        Assert.Equal("Weekly sync v2", ChatUiFormat.SourcesLine([("Weekly sync", "v2")]));
        Assert.Equal("", ChatUiFormat.SourcesLine([]));
    }

    [Fact]
    public void SourcesSummary_ShouldPluralise()
    {
        Assert.Equal("1 recording", ChatUiFormat.SourcesSummary(1));
        Assert.Equal("3 recordings", ChatUiFormat.SourcesSummary(3));
        Assert.Equal("No recordings", ChatUiFormat.SourcesSummary(0));
    }

    [Fact]
    public void CitationLabel_ShouldShowRecordingAndTime()
    {
        Assert.Equal("Weekly sync 03:20", ChatUiFormat.CitationLabel(new ChatCitation { RecordingId = "a", StartSec = 200 }, Title));
        Assert.Equal("Deleted recording 00:05", ChatUiFormat.CitationLabel(new ChatCitation { RecordingId = "gone", StartSec = 5 }, Title));
        var shortened = ChatUiFormat.CitationLabel(new ChatCitation { RecordingId = "c", StartSec = 725 }, Title);
        Assert.EndsWith("… 12:05", shortened);
        Assert.True(shortened.Length <= ChatUiFormat.CitationTitleMax + 7);
    }

    [Fact]
    public void RowMeta_ShouldNameASingleSourceAndCountSeveral()
    {
        var one = new ChatRecord { UpdatedAt = Now, Sources = [new ChatSource("a", "v1", Now)], Messages = [Msg("user", "q"), Msg("assistant", "a")] };
        Assert.Equal("Weekly sync · 2 messages · 16:00", ChatUiFormat.RowMeta(one, Title, Now));

        var many = new ChatRecord
        {
            UpdatedAt = Now.AddDays(-1),
            Sources = [new ChatSource("a", "v1", Now), new ChatSource("b", "v1", Now), new ChatSource("c", "v1", Now)],
            Messages = [Msg("user", "q"), Msg("system", "added")],
        };
        Assert.Equal("3 recordings · 1 message · Yesterday", ChatUiFormat.RowMeta(many, Title, Now));
    }

    [Fact]
    public void LastLine_ShouldPreferTheLastAnswerAndCollapseWhitespace()
    {
        var chat = new ChatRecord { Messages = [Msg("user", "q"), Msg("assistant", "Line one\n\nline   two"), Msg("user", "next")] };
        Assert.Equal("Line one line two", ChatUiFormat.LastLine(chat));
        Assert.Equal("", ChatUiFormat.LastLine(new ChatRecord()));
    }

    [Fact]
    public void Build_ShouldGroupRunsAndMarkEvents()
    {
        var chat = new ChatRecord
        {
            Messages =
            [
                Msg("user", "one", 1),
                Msg("user", "two", 2),
                Msg("assistant", "answer", 3, new ChatCitation { RecordingId = "a", StartSec = 200 }, new ChatCitation { RecordingId = "a", StartSec = 200 }),
                Msg("system", "Supplier call added · Gemma now reads 2 recordings", 4),
                Msg("user", "three", 5),
            ],
        };

        var items = ChatUiThread.Build(chat, Title);

        Assert.Equal([true, false, true, false, true], items.Select(i => i.ShowRoleLabel));
        Assert.Equal([false, true, true, false, true], items.Select(i => i.ShowTime));
        Assert.Equal(ChatThreadKind.Event, items[3].Kind);
        Assert.Single(items[2].Citations); // identical chips collapse
        Assert.Equal("Weekly sync 03:20", items[2].Citations[0].Label);
        Assert.True(items[2].Citations[0].RecordingExists);
    }

    [Fact]
    public void Build_ShouldDisableChipsOfDeletedRecordings()
    {
        var chat = new ChatRecord { Messages = [Msg("assistant", "x", 1, new ChatCitation { RecordingId = "gone", StartSec = 1 })] };
        var chip = ChatUiThread.Build(chat, Title)[0].Citations[0];
        Assert.False(chip.RecordingExists);
        Assert.Contains("deleted", chip.AutomationName);
    }

    [Fact]
    public void IsIncomplete_ShouldReadFinishReasonsFromExtensionData()
    {
        var cancelled = Msg("assistant", "partial");
        cancelled.ExtensionData = new() { ["finish"] = JsonDocument.Parse("\"cancelled\"").RootElement.Clone() };
        var stop = Msg("assistant", "done");
        stop.ExtensionData = new() { ["finish"] = JsonDocument.Parse("\"stop\"").RootElement.Clone() };
        var flagged = Msg("assistant", "x");
        flagged.ExtensionData = new() { ["incomplete"] = JsonDocument.Parse("true").RootElement.Clone() };

        Assert.True(ChatUiFormat.IsIncomplete(cancelled));
        Assert.False(ChatUiFormat.IsIncomplete(stop));
        Assert.True(ChatUiFormat.IsIncomplete(flagged));
        Assert.False(ChatUiFormat.IsIncomplete(Msg("assistant", "plain")));
    }

    [Fact]
    public void OutdatedNotices_ShouldNameOldAndCurrentVersion()
    {
        var chat = new ChatRecord { Sources = [new ChatSource("a", "v1", Now), new ChatSource("b", "v2", Now)] };
        var notices = ChatUiThread.OutdatedNotices(chat, id => id == "a" ? "v3" : "v2", Title);
        var notice = Assert.Single(notices);
        Assert.Equal("a", notice.RecordingId);
        Assert.Contains("answers use v1, current is v3", notice.Text);
        Assert.True(notice.CanUseCurrent);
    }

    [Fact]
    public void Filter_ShouldMatchTitleMessagesAndSourceNames()
    {
        var a = new ChatRecord { Id = "1", Title = "Q4 planning", Sources = [new ChatSource("a", "v1", Now)], Messages = [Msg("user", "budget?")] };
        var b = new ChatRecord { Id = "2", Title = "Other", Sources = [new ChatSource("b", "v1", Now)] };
        var chats = new[] { a, b };

        Assert.Equal(["1"], ChatUiFormat.Filter(chats, "planning BUDGET", Title).Select(c => c.Id));
        Assert.Equal(["2"], ChatUiFormat.Filter(chats, "supplier", Title).Select(c => c.Id));
        Assert.Equal(2, ChatUiFormat.Filter(chats, "  ", Title).Count);
    }

    [Fact]
    public void ContextLine_ShouldDescribeWhatGemmaReads()
    {
        Assert.Equal("Reads transcript v2 · 12:34", ChatUiFormat.ContextLine([("v2", 754)]));
        Assert.StartsWith("Reads a summary of each", ChatUiFormat.ContextLine([("v1", 10), ("v1", 20)]));
        Assert.Equal("Add a recording to continue", ChatUiFormat.ContextLine([]));
    }

    [Fact]
    public void Markdown_ShouldParseParagraphsListsHeadingsAndInlineStyles()
    {
        var blocks = ChatMarkdown.Parse("# Risks\nThe **release** may slip,\nsee `plan`.\n\n- first *item*\n- second\n1. numbered");

        Assert.Equal([ChatMdBlockKind.Heading, ChatMdBlockKind.Paragraph, ChatMdBlockKind.Bullet, ChatMdBlockKind.Bullet, ChatMdBlockKind.Bullet],
            blocks.Select(b => b.Kind));
        var paragraph = blocks[1].Runs;
        Assert.Equal(ChatMdStyle.Bold, paragraph[1].Style);
        Assert.Equal("release", paragraph[1].Text);
        Assert.Equal(ChatMdStyle.Code, paragraph[3].Style);
        Assert.Equal(ChatMdStyle.Italic, blocks[2].Runs[1].Style);
        Assert.Equal("numbered", blocks[4].Runs[0].Text);
    }

    [Fact]
    public void Markdown_ShouldKeepUnbalancedMarkersAsText()
    {
        var runs = ChatMarkdown.Inline("2 * 3 = 6 and **open");
        Assert.All(runs, r => Assert.Equal(ChatMdStyle.Plain, r.Style));
        Assert.Equal("2 * 3 = 6 and **open", string.Concat(runs.Select(r => r.Text)));
    }
}
