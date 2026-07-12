using SpeechLib;
using SpeechLib.Models;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// E2E tests for DiarizationMergeService — merging ASR word timestamps
/// with diarization speaker segments, plus formatting.
/// Uses real RTTM files from DiarizationConverter/dataset as ground truth.
/// </summary>
public sealed class E2E_DiarizationMergeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string DatasetDir = Path.Combine(
        RepoRoot, "DiarizationConverter", "dataset");

    private static readonly string RttmDir = Path.Combine(DatasetDir, "rttm");

    private static bool CanRunRttm => Directory.Exists(RttmDir) &&
        Directory.GetFiles(RttmDir, "*.rttm").Length > 0;

    // ═══════════════════════════════════════════════════════════════
    // Unit: Merge logic
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Merge_EmptyWords_ReturnsEmptyList()
    {
        var words = Array.Empty<WordTiming>();
        var segments = new List<DiarizationSegment>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 10 }
        };

        var result = DiarizationMergeService.Merge(words, segments);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_EmptySegments_ReturnsSingleUnknownUtterance()
    {
        var words = new List<WordTiming>
        {
            new() { Word = "hello", StartSeconds = 0, EndSeconds = 0.5 },
            new() { Word = "world", StartSeconds = 0.6, EndSeconds = 1.1 }
        };

        var result = DiarizationMergeService.Merge(words, new List<DiarizationSegment>());

        Assert.Single(result);
        Assert.Equal("SPEAKER_??", result[0].SpeakerId);
        Assert.Equal("hello world", result[0].Text);
        Assert.Equal(0, result[0].StartSeconds);
        Assert.Equal(1.1, result[0].EndSeconds);
    }

    [Fact]
    public void Merge_SingleSpeaker_AllWords_SingleUtterance()
    {
        var words = new List<WordTiming>
        {
            new() { Word = "a", StartSeconds = 0.0, EndSeconds = 0.2 },
            new() { Word = "b", StartSeconds = 0.3, EndSeconds = 0.5 },
            new() { Word = "c", StartSeconds = 0.6, EndSeconds = 0.8 }
        };
        var segments = new List<DiarizationSegment>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 1.0 }
        };

        var result = DiarizationMergeService.Merge(words, segments);

        Assert.Single(result);
        Assert.Equal("SPEAKER_00", result[0].SpeakerId);
        Assert.Equal("a b c", result[0].Text);
        Assert.Equal(0, result[0].StartSeconds);
        Assert.Equal(0.8, result[0].EndSeconds);
    }

    [Fact]
    public void Merge_TwoSpeakers_SplitsUtterances()
    {
        var words = new List<WordTiming>
        {
            new() { Word = "hello", StartSeconds = 0.0, EndSeconds = 0.5 },
            new() { Word = "there", StartSeconds = 0.6, EndSeconds = 1.1 },
            new() { Word = "how", StartSeconds = 2.0, EndSeconds = 2.3 },
            new() { Word = "are", StartSeconds = 2.4, EndSeconds = 2.7 },
            new() { Word = "you", StartSeconds = 2.8, EndSeconds = 3.1 }
        };
        var segments = new List<DiarizationSegment>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 1.5 },
            new() { SpeakerId = "SPEAKER_01", StartSeconds = 1.5, EndSeconds = 4.0 }
        };

        var result = DiarizationMergeService.Merge(words, segments);

        Assert.Equal(2, result.Count);

        Assert.Equal("SPEAKER_00", result[0].SpeakerId);
        Assert.Equal("hello there", result[0].Text);
        Assert.Equal(0, result[0].StartSeconds);
        Assert.Equal(1.1, result[0].EndSeconds);

        Assert.Equal("SPEAKER_01", result[1].SpeakerId);
        Assert.Equal("how are you", result[1].Text);
        Assert.Equal(2.0, result[1].StartSeconds);
        Assert.Equal(3.1, result[1].EndSeconds);
    }

    [Fact]
    public void Merge_SpeakerSwitch_BackAndForth()
    {
        var words = new List<WordTiming>
        {
            new() { Word = "A1", StartSeconds = 0.0, EndSeconds = 0.3 },
            new() { Word = "A2", StartSeconds = 0.4, EndSeconds = 0.7 },
            new() { Word = "B1", StartSeconds = 1.0, EndSeconds = 1.3 },
            new() { Word = "A3", StartSeconds = 2.0, EndSeconds = 2.3 }
        };
        var segments = new List<DiarizationSegment>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 0.8 },
            new() { SpeakerId = "SPEAKER_01", StartSeconds = 0.8, EndSeconds = 1.5 },
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 1.5, EndSeconds = 3.0 }
        };

        var result = DiarizationMergeService.Merge(words, segments);

        Assert.Equal(3, result.Count);
        Assert.Equal("SPEAKER_00", result[0].SpeakerId);
        Assert.Equal("A1 A2", result[0].Text);

        Assert.Equal("SPEAKER_01", result[1].SpeakerId);
        Assert.Equal("B1", result[1].Text);

        Assert.Equal("SPEAKER_00", result[2].SpeakerId);
        Assert.Equal("A3", result[2].Text);
    }

    // ═══════════════════════════════════════════════════════════════
    // Unit: Timestamp formatting
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, 0, "[00:00.0]")]
    [InlineData(3.2, 0, "[00:03.2]")]
    [InlineData(10.5, 0, "[00:10.5]")]
    [InlineData(65.0, 0, "[01:05.0]")]
    [InlineData(125.7, 0, "[02:05.7]")]
    [InlineData(3599.9, 0, "[59:59.9]")]
    public void FormatTimestamp_ShouldProduceCorrectFormat(double seconds, double _, string expected)
    {
        var result = DiarizedUtterance.FormatTimestamp(seconds);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DiarizedUtterance_ToString_ShouldIncludeAllParts()
    {
        var u = new DiarizedUtterance
        {
            SpeakerId = "SPEAKER_01",
            StartSeconds = 5.25,
            EndSeconds = 12.75,
            Text = "hello world"
        };

        var result = u.ToString();
        Assert.Equal("[00:05.3] SPEAKER_01: hello world", result);
    }

    [Fact]
    public void DiarizedUtterance_Duration_ShouldBeCorrect()
    {
        var u = new DiarizedUtterance
        {
            StartSeconds = 5.0,
            EndSeconds = 12.5
        };

        Assert.Equal(7.5, u.DurationSeconds, precision: 3);
    }

    // ═══════════════════════════════════════════════════════════════
    // Unit: FormatForPreview
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatForPreview_EmptyList_ReturnsEmptyString()
    {
        var result = DiarizationMergeService.FormatForPreview(new List<DiarizedUtterance>());
        Assert.Equal("", result);
    }

    [Fact]
    public void FormatForPreview_SingleUtterance_NoLeadingNewline()
    {
        var utterances = new List<DiarizedUtterance>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 2, Text = "hello" }
        };

        var result = DiarizationMergeService.FormatForPreview(utterances);

        // Must NOT start with a newline
        Assert.False(result.StartsWith('\n') || result.StartsWith("\r\n"));
        Assert.Equal("[00:00.0] SPEAKER_00: hello", result);
    }

    [Fact]
    public void FormatForPreview_MultipleUtterances_NewlineSeparated()
    {
        var utterances = new List<DiarizedUtterance>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 2, Text = "hello" },
            new() { SpeakerId = "SPEAKER_01", StartSeconds = 3, EndSeconds = 5, Text = "world" }
        };

        var result = DiarizationMergeService.FormatForPreview(utterances);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("SPEAKER_00", lines[0]);
        Assert.Contains("SPEAKER_01", lines[1]);
    }

    // ═══════════════════════════════════════════════════════════════
    // Unit: ExtractPlainText
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractPlainText_ShouldStripTimestampsAndSpeakers()
    {
        var utterances = new List<DiarizedUtterance>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 2, Text = "hello there" },
            new() { SpeakerId = "SPEAKER_01", StartSeconds = 3, EndSeconds = 5, Text = "how are you" }
        };

        var result = DiarizationMergeService.ExtractPlainText(utterances);

        Assert.Equal("hello there how are you", result);
        Assert.DoesNotContain("SPEAKER", result);
        Assert.DoesNotContain("[", result);
    }

    // ═══════════════════════════════════════════════════════════════
    // E2E: Real RTTM files → DiarizationSegment
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RttmFiles_ParseToSegments_ShouldProduceValidSegments()
    {
        if (!CanRunRttm) return;

        var rttmFiles = Directory.GetFiles(RttmDir, "*.rttm");
        Assert.NotEmpty(rttmFiles);

        foreach (var rttmPath in rttmFiles.Take(5))
        {
            var segments = ParseRttmToSegments(rttmPath);
            Assert.NotEmpty(segments);

            foreach (var seg in segments)
            {
                Assert.False(string.IsNullOrEmpty(seg.SpeakerId));
                Assert.True(seg.EndSeconds > seg.StartSeconds,
                    $"Segment {seg.SpeakerId}: end {seg.EndSeconds} <= start {seg.StartSeconds}");
            }
        }
    }

    [Fact]
    public void Merge_WithRealRttmSegments_ShouldAssignSpeakers()
    {
        if (!CanRunRttm) return;

        // Use first RTTM file as ground truth
        var rttmPath = Directory.GetFiles(RttmDir, "*.rttm").First();
        var segments = ParseRttmToSegments(rttmPath);

        // Create synthetic words spanning the segment time range
        double maxTime = segments.Max(s => s.EndSeconds);
        var words = new List<WordTiming>();
        double t = 0;
        while (t < maxTime)
        {
            words.Add(new WordTiming
            {
                Word = $"w_{words.Count}",
                StartSeconds = t,
                EndSeconds = t + 0.3
            });
            t += 0.4;
        }

        var result = DiarizationMergeService.Merge(words, segments);

        // Every word should have a speaker assigned (not SPEAKER_??)
        Assert.All(result, u => Assert.NotEqual("SPEAKER_??", u.SpeakerId));

        // Utterances should be in time order
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i].StartSeconds >= result[i - 1].StartSeconds - 0.001,
                $"Utterance order violation at index {i}");
    }

    [Fact]
    public void Merge_WithRealRttm_BoundaryWords_CorrectSpeaker()
    {
        if (!CanRunRttm) return;

        // Pick an RTTM file with multiple speakers (at least 2 speaker IDs)
        var rttmPath = Directory.GetFiles(RttmDir, "*.rttm")
            .FirstOrDefault(f => HasMultipleSpeakers(f));

        if (rttmPath is null) return; // All single-speaker files — skip

        var segments = ParseRttmToSegments(rttmPath);

        // Create words exactly at segment boundaries
        var words = new List<WordTiming>();
        foreach (var seg in segments)
        {
            double mid = (seg.StartSeconds + seg.EndSeconds) / 2.0;
            words.Add(new WordTiming
            {
                Word = $"word_at_{mid:F1}",
                StartSeconds = mid - 0.1,
                EndSeconds = mid + 0.1
            });
        }

        var result = DiarizationMergeService.Merge(words, segments);

        // Each utterance's speaker should match its segment
        for (int i = 0; i < result.Count; i++)
        {
            // Find the dominant segment for this utterance's midpoint
            double midUtterance = (result[i].StartSeconds + result[i].EndSeconds) / 2.0;

            var dominantSeg = segments
                .Where(s => midUtterance >= s.StartSeconds && midUtterance <= s.EndSeconds)
                .MaxBy(s => s.EndSeconds - s.StartSeconds);

            if (dominantSeg is not null)
            {
                Assert.Equal(dominantSeg.SpeakerId, result[i].SpeakerId);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // E2E: Timestamp edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Timestamps_AreMonotonicallyIncreasing_AcrossUtterances()
    {
        var words = new List<WordTiming>();
        for (int i = 0; i < 50; i++)
        {
            words.Add(new WordTiming
            {
                Word = $"w{i}",
                StartSeconds = i * 0.3,
                EndSeconds = i * 0.3 + 0.25
            });
        }
        var segments = new List<DiarizationSegment>
        {
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 0, EndSeconds = 5 },
            new() { SpeakerId = "SPEAKER_01", StartSeconds = 5, EndSeconds = 10 },
            new() { SpeakerId = "SPEAKER_00", StartSeconds = 10, EndSeconds = 15 }
        };

        var result = DiarizationMergeService.Merge(words, segments);
        var formatted = DiarizationMergeService.FormatForPreview(result);

        // Every line should start with a timestamp bracket
        var lines = formatted.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.StartsWith("[", line));

        // Timestamps should be monotonically increasing
        var timestamps = lines
            .Select(line => ParseTimestampFromLine(line))
            .ToList();
        for (int i = 1; i < timestamps.Count; i++)
            Assert.True(timestamps[i] >= timestamps[i - 1],
                $"Timestamp order violation: {timestamps[i - 1]} → {timestamps[i]}");
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Parse RTTM file into DiarizationSegment list (SpeechLib type).</summary>
    private static List<DiarizationSegment> ParseRttmToSegments(string rttmPath)
    {
        var segments = new List<DiarizationSegment>();
        foreach (var line in File.ReadLines(rttmPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || parts[0] != "SPEAKER")
                continue;

            // SPEAKER <file> 1 <start> <duration> <NA> <NA> <speaker_id> <NA> <NA>
            if (double.TryParse(parts[3], out var start) &&
                double.TryParse(parts[4], out var duration))
            {
                segments.Add(new DiarizationSegment
                {
                    SpeakerId = $"SPEAKER_{parts[7]}",
                    StartSeconds = start,
                    EndSeconds = start + duration
                });
            }
        }
        return segments;
    }

    private static bool HasMultipleSpeakers(string rttmPath)
    {
        var segments = ParseRttmToSegments(rttmPath);
        return segments.Select(s => s.SpeakerId).Distinct().Count() >= 2;
    }

    private static double ParseTimestampFromLine(string line)
    {
        // "[MM:SS.F] SPEAKER: text" → extract seconds
        var bracketEnd = line.IndexOf(']');
        if (bracketEnd < 0) return 0;
        var ts = line[1..bracketEnd]; // "MM:SS.F"
        var parts = ts.Split(':');
        if (parts.Length != 2) return 0;
        return int.Parse(parts[0]) * 60 + double.Parse(parts[1],
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
