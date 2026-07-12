using SpeechLib.Models;
using System.Text;

namespace SpeechLib;

/// <summary>
/// Merges ASR word-level timestamps with diarization speaker segments.
/// Assigns each word to the best-matching speaker based on time overlap.
/// </summary>
public static class DiarizationMergeService
{
    /// <summary>
    /// Assign each word timing to the speaker segment with the best time overlap,
    /// then group consecutive same-speaker words into utterances.
    /// </summary>
    /// <param name="words">ASR word timings with text.</param>
    /// <param name="segments">Diarization speaker segments.</param>
    /// <returns>Merged utterances grouped by speaker.</returns>
    public static List<DiarizedUtterance> Merge(
        IReadOnlyList<WordTiming> words,
        IReadOnlyList<DiarizationSegment> segments)
    {
        if (words.Count == 0) return new List<DiarizedUtterance>();
        if (segments.Count == 0)
        {
            // No diarization — return a single "SPEAKER_??" utterance
            return new List<DiarizedUtterance>
            {
                new()
                {
                    SpeakerId = "SPEAKER_??",
                    StartSeconds = words[0].StartSeconds,
                    EndSeconds = words[^1].EndSeconds,
                    Text = string.Join(" ", words.Select(w => w.Word))
                }
            };
        }

        // Step 1: Assign speaker to each word
        var wordSpeakers = new List<(WordTiming Word, string Speaker)>(words.Count);
        foreach (var word in words)
        {
            double wordMid = (word.StartSeconds + word.EndSeconds) / 2.0;
            string speaker = FindBestSpeaker(wordMid, segments);
            wordSpeakers.Add((word, speaker));
        }

        // Step 2: Group consecutive same-speaker words into utterances
        var utterances = new List<DiarizedUtterance>();
        if (wordSpeakers.Count == 0) return utterances;

        var currentSpeaker = wordSpeakers[0].Speaker;
        var currentWords = new List<string> { wordSpeakers[0].Word.Word };
        double currentStart = wordSpeakers[0].Word.StartSeconds;
        double currentEnd = wordSpeakers[0].Word.EndSeconds;

        for (int i = 1; i < wordSpeakers.Count; i++)
        {
            var (word, speaker) = wordSpeakers[i];

            if (speaker == currentSpeaker)
            {
                // Same speaker — accumulate
                currentWords.Add(word.Word);
                currentEnd = word.EndSeconds;
            }
            else
            {
                // Speaker change — flush current utterance
                utterances.Add(new DiarizedUtterance
                {
                    SpeakerId = currentSpeaker,
                    StartSeconds = currentStart,
                    EndSeconds = currentEnd,
                    Text = string.Join(" ", currentWords)
                });

                // Start new utterance
                currentSpeaker = speaker;
                currentWords = new List<string> { word.Word };
                currentStart = word.StartSeconds;
                currentEnd = word.EndSeconds;
            }
        }

        // Flush last utterance
        utterances.Add(new DiarizedUtterance
        {
            SpeakerId = currentSpeaker,
            StartSeconds = currentStart,
            EndSeconds = currentEnd,
            Text = string.Join(" ", currentWords)
        });

        return utterances;
    }

    /// <summary>
    /// Format merged utterances for preview window display.
    /// Each utterance on its own line: "[00:03.2] SPEAKER_00: text".
    /// </summary>
    public static string FormatForPreview(IReadOnlyList<DiarizedUtterance> utterances)
    {
        var sb = new StringBuilder();
        foreach (var u in utterances)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(u.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extract plain text without speaker labels or timestamps —
    /// suitable for text injection.
    /// </summary>
    public static string ExtractPlainText(IReadOnlyList<DiarizedUtterance> utterances)
    {
        return string.Join(" ", utterances.Select(u => u.Text));
    }

    // ── Private helpers ──────────────────────────────────────────

    /// <summary>
    /// Find the speaker segment that best overlaps with the given time point.
    /// Uses the segment with the longest duration that contains the point.
    /// Falls back to nearest segment if no exact overlap.
    /// </summary>
    private static string FindBestSpeaker(double timePoint, IReadOnlyList<DiarizationSegment> segments)
    {
        DiarizationSegment? best = null;
        double bestDuration = -1;

        foreach (var seg in segments)
        {
            if (timePoint >= seg.StartSeconds && timePoint <= seg.EndSeconds)
            {
                double dur = seg.EndSeconds - seg.StartSeconds;
                if (dur > bestDuration)
                {
                    bestDuration = dur;
                    best = seg;
                }
            }
        }

        if (best is not null)
            return best.SpeakerId;

        // No exact overlap — find nearest segment
        double minDist = double.MaxValue;
        foreach (var seg in segments)
        {
            double dist = Math.Min(
                Math.Abs(timePoint - seg.StartSeconds),
                Math.Abs(timePoint - seg.EndSeconds));
            if (dist < minDist)
            {
                minDist = dist;
                best = seg;
            }
        }

        return best?.SpeakerId ?? "SPEAKER_??";
    }
}
