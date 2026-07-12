using DiarizationTest.Models;

namespace DiarizationTest;

/// <summary>
/// Calculates Diarization Error Rate (DER) between predicted speaker segments and RTTM ground truth.
/// 
/// DER = (Missed Speech + False Alarm + Speaker Confusion) / Total Ground Truth Speech
/// 
/// Reference: NIST RT-09 evaluation plan
///   - Missed Speech:   ground truth speech not assigned to any speaker
///   - False Alarm:     predicted speech where no one was actually speaking
///   - Speaker Confusion: correct speech but wrong speaker label
/// </summary>
public static class DiarizationEvaluator
{
    /// <summary>
    /// Parse an RTTM file into speaker segments.
    /// RTTM format: SPEAKER <file_id> <channel> <start> <duration> <NA> <NA> <speaker_id> <NA> <NA>
    /// </summary>
    public static List<SpeakerSegment> ParseRttm(string rttmPath)
    {
        var segments = new List<SpeakerSegment>();
        foreach (var line in File.ReadLines(rttmPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || parts[0] != "SPEAKER")
                continue;

            if (double.TryParse(parts[3], out double start) &&
                double.TryParse(parts[4], out double duration))
            {
                segments.Add(new SpeakerSegment(parts[7], start, start + duration));
            }
        }
        return segments.OrderBy(s => s.StartSeconds).ToList();
    }

    /// <summary>
    /// Calculate DER between predicted and ground truth segments.
    /// </summary>
    public static DerResult CalculateDer(List<SpeakerSegment> predicted, List<SpeakerSegment> groundTruth,
                                          double collar = 0.25, bool skipOverlap = true)
    {
        // Build frame-level labels for scoring
        double totalDuration = 0;
        if (groundTruth.Count > 0)
            totalDuration = groundTruth.Max(s => s.EndSeconds);
        if (predicted.Count > 0)
            totalDuration = Math.Max(totalDuration, predicted.Max(s => s.EndSeconds));

        double frameStep = 0.01; // 10ms frames
        int numFrames = (int)Math.Ceiling(totalDuration / frameStep);

        // Build ground truth frame labels
        var gtFrames = new int[numFrames]; // 0=no speech, 1..N=speaker123...
        var speakerMap = new Dictionary<string, int>();
        int nextSpeakerId = 1;

        foreach (var seg in groundTruth)
        {
            if (!speakerMap.ContainsKey(seg.SpeakerId))
                speakerMap[seg.SpeakerId] = nextSpeakerId++;

            int spkId = speakerMap[seg.SpeakerId];
            int startFrame = (int)(seg.StartSeconds / frameStep);
            int endFrame = (int)(seg.EndSeconds / frameStep);

            for (int f = startFrame; f < endFrame && f < numFrames; f++)
            {
                if (skipOverlap && gtFrames[f] != 0)
                    continue; // Skip overlapping regions per NIST standard
                gtFrames[f] = spkId;
            }
        }

        // Build predicted frame labels
        var predFrames = new int[numFrames];
        int predSpeakerId = 1;
        var predSpeakerMap = new Dictionary<string, int>();

        foreach (var seg in predicted)
        {
            if (!predSpeakerMap.ContainsKey(seg.SpeakerId))
                predSpeakerMap[seg.SpeakerId] = predSpeakerId++;

            int spkId = predSpeakerMap[seg.SpeakerId];
            int startFrame = (int)(seg.StartSeconds / frameStep);
            int endFrame = (int)(seg.EndSeconds / frameStep);

            for (int f = startFrame; f < endFrame && f < numFrames; f++)
            {
                predFrames[f] = spkId;
            }
        }

        // Calculate errors with collar (ignore errors within collar of segment boundaries)
        int collarFrames = (int)(collar / frameStep);
        double totalSpeech = 0;    // Frames where ground truth has speech
        double missedSpeech = 0;   // GT has speech, pred does not
        double falseAlarm = 0;     // Pred has speech, GT does not
        double confusion = 0;      // Both have speech but different speakers

        for (int f = 0; f < numFrames; f++)
        {
            bool gtSpeech = gtFrames[f] != 0;
            bool predSpeech = predFrames[f] != 0;

            if (gtSpeech) totalSpeech++;

            if (gtSpeech && !predSpeech)
            {
                // Check collar: is there pred speech nearby?
                bool nearPred = false;
                for (int c = Math.Max(0, f - collarFrames);
                     c <= Math.Min(numFrames - 1, f + collarFrames); c++)
                {
                    if (predFrames[c] != 0) { nearPred = true; break; }
                }
                if (!nearPred) missedSpeech++;
            }
            else if (!gtSpeech && predSpeech)
            {
                bool nearGt = false;
                for (int c = Math.Max(0, f - collarFrames);
                     c <= Math.Min(numFrames - 1, f + collarFrames); c++)
                {
                    if (gtFrames[c] != 0) { nearGt = true; break; }
                }
                if (!nearGt) falseAlarm++;
            }
            else if (gtSpeech && predSpeech && gtFrames[f] != predFrames[f])
            {
                confusion++;
            }
        }

        double der = totalSpeech > 0
            ? (missedSpeech + falseAlarm + confusion) / totalSpeech * 100.0
            : 0.0;

        return new DerResult(
            DerPercent: der,
            MissedSpeechPercent: totalSpeech > 0 ? missedSpeech / totalSpeech * 100.0 : 0,
            FalseAlarmPercent: totalSpeech > 0 ? falseAlarm / totalSpeech * 100.0 : 0,
            SpeakerConfusionPercent: totalSpeech > 0 ? confusion / totalSpeech * 100.0 : 0,
            TotalSpeechSeconds: totalSpeech * frameStep,
            NumSpeakersGt: speakerMap.Count,
            NumSpeakersPred: predSpeakerMap.Count
        );
    }
}

/// <summary>
/// Diarization Error Rate result.
/// </summary>
public readonly record struct DerResult(
    double DerPercent,
    double MissedSpeechPercent,
    double FalseAlarmPercent,
    double SpeakerConfusionPercent,
    double TotalSpeechSeconds,
    int NumSpeakersGt,
    int NumSpeakersPred
)
{
    public override string ToString() =>
        $"DER={DerPercent:F2}% | Miss={MissedSpeechPercent:F2}% | FA={FalseAlarmPercent:F2}% | " +
        $"Conf={SpeakerConfusionPercent:F2}% | Speech={TotalSpeechSeconds:F1}s | " +
        $"Speakers GT:{NumSpeakersGt} Pred:{NumSpeakersPred}";
}
