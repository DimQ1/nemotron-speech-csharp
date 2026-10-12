using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class SpeakersUiTests
{
    private static readonly SpeakerPerson Anna = new("p0", "Anna", 0);
    private static readonly SpeakerPerson Mark = new("p1", "Mark", 1);
    private static readonly SpeakerPerson Oleg = new("p2", "Oleg", 2);

    /// <summary>Segments: Anna, Anna, Mark, Mark, Anna, Oleg (6 segments).</summary>
    private static SpeakerLayerView Layer(params SpeakerPerson[] people)
    {
        string[] persons = ["p0", "p0", "p1", "p1", "p0", "p2"];
        var segments = persons.Select((p, i) => new SegmentSpeakers(i, p, false, false, [p, p])).ToList();
        var list = people.Length == 0 ? [Anna, Mark, Oleg] : people;
        var stats = new List<SpeakerStat> { new("p0", 347, 60, 2), new("p1", 200, 35, 1), new("p2", 20, 2.4, 1) };
        return new SpeakerLayerView("s1", "r1", "v1", "Nemotron Diarization INT8", "offline, 30 s buffer", list, segments, stats);
    }

    [Fact]
    public void Palette_ShouldHaveEightDistinctColorsPerTheme()
    {
        for (var theme = 0; theme < 2; theme++)
        {
            var colors = Enumerable.Range(0, SpeakersUi.PaletteSize).Select(i => SpeakersUi.ColorArgb(i, theme == 1)).ToList();
            Assert.Equal(8, colors.Distinct().Count());
        }
        Assert.NotEqual(SpeakersUi.ColorArgb(0, dark: false), SpeakersUi.ColorArgb(0, dark: true));
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(-1, 7)]
    [InlineData(19, 3)]
    public void NormalizeColor_ShouldWrapIntoThePalette(int index, int expected) =>
        Assert.Equal(expected, SpeakersUi.NormalizeColor(index));

    [Fact]
    public void Palette_ShouldHaveReadableContrastForTextOnTheThemeBackground()
    {
        // Light tones are used as text on white, dark tones as text on near-black: ≥ 3:1 (large/bold text).
        for (var i = 0; i < SpeakersUi.PaletteSize; i++)
        {
            Assert.True(Contrast(SpeakersUi.ColorArgb(i, false), 0xFFFFFFFF) >= 3.0, "light " + i);
            Assert.True(Contrast(SpeakersUi.ColorArgb(i, true), 0xFF202020) >= 3.0, "dark " + i);
        }
    }

    private static double Contrast(uint a, uint b)
    {
        static double L(uint argb)
        {
            static double C(int v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * C((int)(argb >> 16 & 0xFF)) + 0.7152 * C((int)(argb >> 8 & 0xFF)) + 0.0722 * C((int)(argb & 0xFF));
        }
        var (hi, lo) = L(a) > L(b) ? (L(a), L(b)) : (L(b), L(a));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void NextFreeColor_ShouldPickAnUnusedEntry() =>
        Assert.Equal(2, SpeakersUi.NextFreeColor([new SpeakerPerson("a", "A", 0), new SpeakerPerson("b", "B", 1)]));

    [Fact]
    public void SummaryLine_ShouldListTheTwoLongestTalkersAndEllipsis() =>
        Assert.Equal("3 speakers · Anna 60 %, Mark 35 %…", SpeakersUi.SummaryLine(Layer()));

    [Fact]
    public void SummaryLine_ShouldNotEllipsizeTwoPeople() =>
        Assert.Equal("2 speakers · Anna 60 %, Mark 35 %", SpeakersUi.SummaryLine(Layer(Anna, Mark)));

    [Fact]
    public void SummaryLine_ShouldSingularizeOnePerson() =>
        Assert.StartsWith("1 speaker · Anna", SpeakersUi.SummaryLine(Layer(Anna)));

    [Fact]
    public void StatLine_ShouldShowPercentTimeAndTurns()
    {
        Assert.Equal("46 % · 5:47 · 7 turns", SpeakersUi.StatLine(new SpeakerStat("p0", 347, 46, 7)));
        Assert.Equal("2 % · 38 s · 1 turn", SpeakersUi.StatLine(new SpeakerStat("p1", 38, 2.4, 1)));
        Assert.Equal("No speech detected", SpeakersUi.StatLine(null));
    }

    [Fact]
    public void Duration_ShouldSwitchToHours() => Assert.Equal("1:05:09", SpeakersUi.Duration(3909));

    [Fact]
    public void MergeHint_ShouldOnlyAppearForVeryShortSpeakers()
    {
        var layer = Layer();
        Assert.Null(SpeakersUi.MergeHint(layer, Anna));
        Assert.Contains("Oleg talks only 20 s", SpeakersUi.MergeHint(layer, Oleg));
    }

    [Theory]
    [InlineData(360, 800, SpeakerBarMode.Summary)]
    [InlineData(599, 800, SpeakerBarMode.Summary)]
    [InlineData(600, 800, SpeakerBarMode.Picker)]
    [InlineData(839, 800, SpeakerBarMode.Picker)]
    [InlineData(840, 480, SpeakerBarMode.Chips)]
    [InlineData(1280, 800, SpeakerBarMode.Chips)]
    [InlineData(800, 360, SpeakerBarMode.Picker)]
    [InlineData(900, 360, SpeakerBarMode.Picker)]
    public void BarMode_ShouldFollowTheWidthBreakpoints(double width, double height, SpeakerBarMode expected) =>
        Assert.Equal(expected, SpeakersUi.BarMode(width, height));

    [Fact]
    public void BuildRows_ShouldHeaderEachRunOfOneSpeaker()
    {
        var rows = SpeakersUi.BuildRows(Layer(), 6, null);
        Assert.Equal(new[] { true, false, true, false, true, true }, rows.Select(r => r.ShowHeader).ToArray());
        Assert.All(rows, r => Assert.False(r.IsHidden));
    }

    [Fact]
    public void BuildRows_ShouldHaveNoHeadersWithoutALayer()
    {
        var rows = SpeakersUi.BuildRows(null, 3, null);
        Assert.All(rows, r => Assert.False(r.ShowHeader));
    }

    [Fact]
    public void BuildRows_ShouldHideOthersAndCollapseTheirRuns()
    {
        var rows = SpeakersUi.BuildRows(Layer(), 6, "p0");
        Assert.Equal(new[] { false, false, true, true, false, true }, rows.Select(r => r.IsHidden).ToArray());
        Assert.Equal("··· 2 segments by Mark", rows[2].CollapsedText);
        Assert.Null(rows[3].CollapsedText);
        Assert.Equal("··· 1 segment by Oleg", rows[5].CollapsedText);
        // A visible segment right after a hidden run gets its header again.
        Assert.True(rows[4].ShowHeader);
        Assert.Equal(3, SpeakersUi.CountShown(rows));
    }

    [Fact]
    public void BuildRows_ShouldNameSeveralHiddenSpeakersInOneRun()
    {
        // Anna filtered; Mark, Oleg hidden in a row.
        string[] persons = ["p0", "p1", "p2", "p0"];
        var segments = persons.Select((p, i) => new SegmentSpeakers(i, p, false, false, [])).ToList();
        var layer = new SpeakerLayerView("s1", "r1", "v1", "m", "offline", [Anna, Mark, Oleg], segments, []);
        var rows = SpeakersUi.BuildRows(layer, 4, "p0");
        Assert.Equal("··· 2 segments by Mark, Oleg", rows[1].CollapsedText);
    }

    [Fact]
    public void FilterLine_ShouldCountShownSegments() =>
        Assert.Equal("Anna only · 4 of 14 segments", SpeakersUi.FilterLine("Anna", 4, 14));

    [Fact]
    public void TurnStarts_ShouldBeMaximalRunsOfOneSpeaker()
    {
        double[] starts = [0, 10, 20, 30, 40, 50];
        Assert.Equal(new[] { 0.0, 40.0 }, SpeakersUi.TurnStarts(Layer(), starts, "p0"));
        Assert.Equal(new[] { 20.0 }, SpeakersUi.TurnStarts(Layer(), starts, "p1"));
    }

    [Fact]
    public void NextTurn_ShouldSkipToTheFollowingTurnAndStopAtTheEnd()
    {
        double[] turns = [0, 40, 90];
        Assert.Equal(40.0, SpeakersUi.NextTurn(turns, 5));
        Assert.Equal(90.0, SpeakersUi.NextTurn(turns, 40));
        Assert.Null(SpeakersUi.NextTurn(turns, 90));
    }

    [Fact]
    public void PreviousTurn_ShouldRestartTheCurrentTurnBeforeGoingBack()
    {
        double[] turns = [0, 40, 90];
        Assert.Equal(40.0, SpeakersUi.PreviousTurn(turns, 60));    // 20 s into the turn at 40: back to its start
        Assert.Equal(0.0, SpeakersUi.PreviousTurn(turns, 40.5));   // just started: the one before
        Assert.Null(SpeakersUi.PreviousTurn(turns, 0.5));
    }

    [Fact]
    public void SegmentIndexAt_ShouldFindTheLastStartedSegment()
    {
        double[] starts = [0, 10, 20];
        Assert.Equal(-1, SpeakersUi.SegmentIndexAt(starts, -1));
        Assert.Equal(1, SpeakersUi.SegmentIndexAt(starts, 19.9));
        Assert.Equal(2, SpeakersUi.SegmentIndexAt(starts, 20));
    }

    [Fact]
    public void BuildRuns_ShouldBePlainWithoutAChangeOrColoring()
    {
        var labels = new SegmentSpeakers(0, "p0", false, false, ["p0", "p0", "p0"]);
        Assert.Empty(SpeakersUi.BuildRuns("one two three", labels, Layer(), colorWords: false));
    }

    [Fact]
    public void BuildRuns_ShouldColorWholeSegmentWhenAsked()
    {
        var labels = new SegmentSpeakers(0, "p0", false, false, ["p0", "p0", "p0"]);
        var runs = SpeakersUi.BuildRuns("one two three", labels, Layer(), colorWords: true);
        var run = Assert.Single(runs);
        Assert.Equal("one two three", run.Text);
        Assert.Equal(0, run.ColorIndex);
    }

    [Fact]
    public void BuildRuns_ShouldMarkAChangeInsideTheSegment()
    {
        var labels = new SegmentSpeakers(0, "p0", true, false, ["p0", "p0", "p1", "p1"]);
        var runs = SpeakersUi.BuildRuns("hello there Mark yes", labels, Layer(), colorWords: false);
        Assert.Equal(new[] { "hello there ", "● Mark ", "Mark yes" }, runs.Select(r => r.Text).ToArray());
        Assert.True(runs[1].IsMarker);
        Assert.Equal(1, runs[1].ColorIndex);
        Assert.Null(runs[0].ColorIndex); // words stay uncoloured without the colour option
    }

    [Fact]
    public void BuildRuns_ShouldTintWordsPerSpeakerWhenColoring()
    {
        var labels = new SegmentSpeakers(0, "p0", true, false, ["p0", "p1"]);
        var runs = SpeakersUi.BuildRuns("hi yes", labels, Layer(), colorWords: true);
        Assert.Equal(0, runs[0].ColorIndex);
        Assert.Equal(1, runs[2].ColorIndex);
    }

    [Fact]
    public void BuildRuns_ShouldKeepUnknownWordsWithThePreviousSpeaker()
    {
        var labels = new SegmentSpeakers(0, "p0", false, false, ["p0", null, "p0"]);
        var runs = SpeakersUi.BuildRuns("a b c", labels, Layer(), colorWords: true);
        Assert.Single(runs);
    }

    [Fact]
    public void HasWordLabels_ShouldRequireAtLeastOneLabelledWord()
    {
        Assert.True(SpeakersUi.HasWordLabels(Layer()));
        var bare = new SpeakerLayerView("s", "r", "v", "m", "p", [Anna], [new SegmentSpeakers(0, "p0", false, false, [])], []);
        Assert.False(SpeakersUi.HasWordLabels(bare));
        Assert.False(SpeakersUi.HasWordLabels(null));
    }

    [Fact]
    public void VersionLine_ShouldNameThePeopleAndModel()
    {
        var layer = Layer();
        Assert.Equal("Speakers 3 · Nemotron Diarization INT8", SpeakersUi.VersionLine(layer));
        Assert.Equal("Detection offline, 30 s buffer", SpeakersUi.PresetLine(layer));
    }

    [Fact]
    public void VoiceLimitWarning_ShouldAppearAtEightPeople()
    {
        var eight = Enumerable.Range(0, 8).Select(i => new SpeakerPerson("p" + i, "S" + i, i)).ToArray();
        Assert.NotNull(SpeakersUi.VoiceLimitWarning(Layer(eight)));
        Assert.Null(SpeakersUi.VoiceLimitWarning(Layer()));
    }
}

public class SpeakerDetectUiTests
{
    [Fact]
    public void Describe_ShouldHideTheRowWithoutAService() =>
        Assert.False(SpeakerDetectUi.Describe(false, false, false, true, null).IsVisible);

    [Fact]
    public void Describe_ShouldHideTheRowWhenALayerExists() =>
        Assert.False(SpeakerDetectUi.Describe(true, true, true, true, null).IsVisible);

    [Fact]
    public void Describe_ShouldExplainAMissingModelAndOpenModels()
    {
        var info = SpeakerDetectUi.Describe(true, false, false, true, null);
        Assert.Equal(SpeakerDetectKind.NoModel, info.Kind);
        Assert.Equal("Open Models", info.ButtonText);
        Assert.True(info.ButtonEnabled);
    }

    [Fact]
    public void Describe_ShouldOfferDetectWhenIdle()
    {
        var info = SpeakerDetectUi.Describe(true, true, false, true, null);
        Assert.Equal(SpeakerDetectKind.Idle, info.Kind);
        Assert.Equal("Detect speakers", info.ButtonText);
        Assert.True(info.ButtonEnabled);
    }

    [Fact]
    public void Describe_ShouldDisableDetectWithoutAudio() =>
        Assert.False(SpeakerDetectUi.Describe(true, true, false, false, null).ButtonEnabled);

    [Fact]
    public void Describe_ShouldShowProgressWhileRunning()
    {
        var info = SpeakerDetectUi.Describe(true, true, false, true, new SpeakerDetectJob(false, false, 0.42, null));
        Assert.Equal(SpeakerDetectKind.Running, info.Kind);
        Assert.True(info.ShowProgress);
        Assert.Equal("42 %", info.Detail);
        Assert.Equal("Speakers · 42 %", SpeakerDetectUi.ChipText(info));
    }

    [Fact]
    public void Describe_ShouldShowQueuedAndFailed()
    {
        Assert.Equal(SpeakerDetectKind.Queued, SpeakerDetectUi.Describe(true, true, false, true, new SpeakerDetectJob(true, false, 0, null)).Kind);
        var failed = SpeakerDetectUi.Describe(true, true, false, true, new SpeakerDetectJob(false, true, 0, "out of memory"));
        Assert.Equal(SpeakerDetectKind.Failed, failed.Kind);
        Assert.Equal("out of memory", failed.Detail);
        Assert.Equal("Try again", failed.ButtonText);
    }

    [Fact]
    public void RunningJobShouldWinOverAMissingModel()
    {
        // A job from the Queue can exist while the model status is momentarily unknown.
        var info = SpeakerDetectUi.Describe(true, false, false, true, new SpeakerDetectJob(false, false, 0.1, null));
        Assert.Equal(SpeakerDetectKind.Running, info.Kind);
    }
}

public class SpeakerExportOptionsTests
{
    private static SpeakerLayerView Layer() => new("s1", "r1", "v1", "m", "offline",
        [new SpeakerPerson("p0", "Anna", 0), new SpeakerPerson("p1", "Mark", 1)],
        [new SegmentSpeakers(0, "p0", false, false, []), new SegmentSpeakers(1, "p1", false, false, []), new SegmentSpeakers(2, null, false, false, [])], []);

    private static TranscriptDocument Transcript()
    {
        var doc = new TranscriptDocument();
        for (var i = 0; i < 3; i++)
            doc.Segments.Add(new TranscriptSegment { Index = i, Start = i * 10, End = i * 10 + 9, Text = " line " + i });
        doc.Text = TranscriptDocument.JoinSegments(doc.Segments);
        return doc;
    }

    [Fact]
    public void Apply_ShouldPrefixLabelledSegmentsOnly()
    {
        var result = SpeakerExportOptions.From(Layer(), includeNames: true).Apply(Transcript());
        Assert.Equal(new[] { "Anna: line 0", "Mark: line 1", "line 2" }, result.Segments.Select(s => s.Text).ToArray());
        Assert.Equal("Anna: line 0 Mark: line 1 line 2", result.Text);
        Assert.Equal(10, result.Segments[1].Start);
    }

    [Fact]
    public void Apply_ShouldReturnTheSameDocumentWhenOff()
    {
        var doc = Transcript();
        Assert.Same(doc, SpeakerExportOptions.From(Layer(), includeNames: false).Apply(doc));
        Assert.Same(doc, SpeakerExportOptions.From(null, includeNames: true).Apply(doc));
    }

    [Fact]
    public void Apply_ShouldGiveTheExportersThePrefixInTxtAndSrt()
    {
        var doc = SpeakerExportOptions.From(Layer(), includeNames: true).Apply(Transcript());
        Assert.Contains("Anna: line 0", TranscriptExport.ToText(doc));
        Assert.Contains("Mark: line 1", TranscriptExport.ToSrt(doc));
    }
}

public class SpeakersModelsUiTests
{
    [Fact]
    public void Status_ShouldPreferTheServiceText()
    {
        Assert.Equal("Nemotron 3 Diarization INT8 · 106 MB", SpeakersModelsUi.Status(true, true, "Nemotron 3 Diarization INT8 · 106 MB"));
        Assert.Equal("Model not installed", SpeakersModelsUi.Status(true, false, null));
        Assert.Contains("not part of this build", SpeakersModelsUi.Status(false, false, null));
    }

    [Fact]
    public void CitationChip_ShouldNameTheSpeakerWhenKnown()
    {
        Assert.Equal("Weekly sync · Anna 03:20", SpeakersModelsUi.CitationChip("Weekly sync", "Anna", "03:20"));
        Assert.Equal("Weekly sync · 03:20", SpeakersModelsUi.CitationChip("Weekly sync", null, "03:20"));
    }

    [Fact]
    public void CitationLabel_ShouldAddTheSpeakerFromTheLayerAtRenderTime()
    {
        var citation = new ChatCitation { RecordingId = "r", VersionId = "v1", StartSec = 200 };
        Assert.Equal("Weekly sync 03:20", ChatUiFormat.CitationLabel(citation, _ => "Weekly sync"));
        Assert.Equal("Weekly sync · Anna 03:20", ChatUiFormat.CitationLabel(citation, _ => "Weekly sync", "Anna"));
    }

    [Fact]
    public void HistorySettings_ShouldCloneTheSpeakersGroup()
    {
        var a = new HistorySettings();
        a.Speakers.AfterStop = SpeakerAutoDetect.Off;
        var b = a.Clone();
        b.Speakers.AfterStop = SpeakerAutoDetect.Best;
        Assert.Equal(SpeakerAutoDetect.Off, a.Speakers.AfterStop);
    }
}
