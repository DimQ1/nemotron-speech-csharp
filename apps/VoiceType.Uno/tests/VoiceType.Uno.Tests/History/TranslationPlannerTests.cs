using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class TranslationPlannerTests
{
    private static int Tokens(string text) => (text.Length + 3) / 4;

    private static List<TranscriptSegment> Segments(params string[] texts) =>
        texts.Select((t, i) => new TranscriptSegment { Index = i, Start = i * 5, End = i * 5 + 4, Text = t }).ToList();

    [Fact]
    public void Split_ShouldGiveOneSentencePerSegmentWhenSegmentsAreSentences()
    {
        var plan = TranslationPlanner.Split(Segments("Okay, let's start.", "The meeting moves to ten.", "Anna, can you send it?"), Tokens);

        Assert.Equal(3, plan.Count);
        Assert.Equal([0, 1, 2], plan.Select(s => s.StartSegment));
        Assert.Equal([0, 1, 2], plan.Select(s => s.EndSegment));
        Assert.Equal("The meeting moves to ten.", plan[1].Text);
    }

    [Fact]
    public void Split_ShouldJoinSentenceThatSpansSegmentsAndKeepItsStartSegment()
    {
        var plan = TranslationPlanner.Split(Segments("We need two more", "people in the backend team", "by March. Then we hire."), Tokens);

        Assert.Equal(2, plan.Count);
        Assert.Equal("We need two more people in the backend team by March.", plan[0].Text);
        Assert.Equal(0, plan[0].StartSegment);
        Assert.Equal(2, plan[0].EndSegment);
        Assert.Equal("Then we hire.", plan[1].Text);
        Assert.Equal(2, plan[1].StartSegment);
    }

    [Fact]
    public void Split_ShouldCutSeveralSentencesOutOfOneSegment()
    {
        var plan = TranslationPlanner.Split(Segments("One. Two! Three? Four"), Tokens);

        Assert.Equal(["One.", "Two!", "Three?", "Four"], plan.Select(s => s.Text));
        Assert.All(plan, s => Assert.Equal(0, s.StartSegment));
    }

    [Fact]
    public void Split_ShouldNotCutDecimalsOrAbbreviationsWithoutSpace()
    {
        var plan = TranslationPlanner.Split(Segments("Pi is 3.14 and v2.1 works."), Tokens);

        Assert.Single(plan);
    }

    [Fact]
    public void Split_ShouldSkipBlankSegmentsAndPiecesWithoutLetters()
    {
        var plan = TranslationPlanner.Split(Segments("", "   ", "Hello there.", "...", "- ", "Bye."), Tokens);

        Assert.Equal(["Hello there.", "Bye."], plan.Select(s => s.Text));
        Assert.Equal([2, 5], plan.Select(s => s.StartSegment));
    }

    [Fact]
    public void Split_ShouldReturnNothingForEmptyTranscript()
    {
        Assert.Empty(TranslationPlanner.Split([], Tokens));
        Assert.Empty(TranslationPlanner.Split(Segments("", " "), Tokens));
    }

    [Fact]
    public void Split_ShouldCutVeryLongUnpunctuatedSegmentIntoBoundedPieces()
    {
        var words = Enumerable.Range(0, 400).Select(i => "слово" + i);
        var text = string.Join(" ", words);

        var plan = TranslationPlanner.Split(Segments(text), Tokens, maxSentenceTokens: 100);

        Assert.True(plan.Count >= 4);
        Assert.All(plan, s => Assert.InRange(s.Tokens, 1, 140));
        // Nothing is lost or reordered.
        Assert.Equal(text, string.Join(" ", plan.Select(s => s.Text)));
    }

    [Fact]
    public void Split_ShouldPreferCommaWhenCuttingLongSentence()
    {
        // 30 words of 7 characters ≈ 53 tokens, limit 30: two pieces of ≈ 105 characters, the comma sits in the last third of the first.
        var text = string.Join(" ", Enumerable.Range(0, 14).Select(i => $"word{i:00}")) + ", " +
                   string.Join(" ", Enumerable.Range(14, 16).Select(i => $"word{i:00}"));

        var plan = TranslationPlanner.Split(Segments(text), Tokens, maxSentenceTokens: 30);

        Assert.True(plan.Count >= 2);
        Assert.EndsWith(",", plan[0].Text);
    }

    [Theory]
    [InlineData("Итак, начнём. Встреча переносится на десять часов утра.", 2)]
    [InlineData("Okay. Das Meeting wird verschoben. Bitte bringt den Bericht mit!", 3)]
    [InlineData("你好。今天开会。", 2)]
    public void Split_ShouldHandleCyrillicGermanAndCjkTerminators(string text, int expected)
    {
        Assert.Equal(expected, TranslationPlanner.Split(Segments(text), Tokens).Count);
    }

    [Fact]
    public void Split_ShouldBeDeterministic()
    {
        var segments = Segments("A b c. D e", "f g. H.");
        var a = TranslationPlanner.Split(segments, Tokens);
        var b = TranslationPlanner.Split(segments, Tokens);

        Assert.Equal(a.Select(s => (s.Text, s.StartSegment)), b.Select(s => (s.Text, s.StartSegment)));
    }

    [Fact]
    public void PlanUnits_ShouldStayWithinBudgetAndCoverEverySentenceOnce()
    {
        var segments = Segments(Enumerable.Range(0, 40).Select(i => $"This is the sentence number {i} with some words.").ToArray());
        var plan = TranslationPlanner.Split(segments, Tokens);

        var units = TranslationPlanner.PlanUnits(plan, 0, systemPromptTokens: 100, maxPromptTokens: 800);

        Assert.All(units, u => Assert.True(u.Count >= 1));
        Assert.All(units.Where(u => u.Count > 1), u => Assert.True(u.PromptTokens <= 800, $"{u}"));
        Assert.Equal(plan.Count, units.Sum(u => u.Count));
        Assert.Equal(Enumerable.Range(0, plan.Count), units.SelectMany(u => Enumerable.Range(u.FirstSentence, u.Count)));
        Assert.True(units.Count > 3 && units.Count < plan.Count, "units hold several sentences");
    }

    [Fact]
    public void PlanUnits_ShouldGiveOversizedSentenceItsOwnUnit()
    {
        var plan = TranslationPlanner.Split(Segments("Short one.", new string('x', 4000) + " end."), Tokens, maxSentenceTokens: 5000);

        var units = TranslationPlanner.PlanUnits(plan, 0, 100, 800);

        Assert.Equal(2, units.Count);
        Assert.All(units, u => Assert.Equal(1, u.Count));
    }

    [Fact]
    public void PlanUnits_ShouldStartFromGivenSentenceForResume()
    {
        var plan = TranslationPlanner.Split(Segments(Enumerable.Range(0, 12).Select(i => $"Sentence {i}.").ToArray()), Tokens);

        var units = TranslationPlanner.PlanUnits(plan, 7, 100, 800);

        Assert.Equal(7, units[0].FirstSentence);
        Assert.Equal(5, units.Sum(u => u.Count));
    }
}
