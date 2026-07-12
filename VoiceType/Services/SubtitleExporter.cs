using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SpeechLib.Models;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Exports transcribed text to subtitle formats: TXT, SRT, VTT.
/// Supports plain text and diarized (speaker-labeled) output.
/// </summary>
public static class SubtitleExporter
{
    /// <summary>Export a single file's result to the specified format.</summary>
    public static void Export(AudioFileJob job, string outputDir, string format)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.FilePath);
        var extension = format.ToLowerInvariant() switch
        {
            "srt" => ".srt",
            "vtt" => ".vtt",
            _ => ".txt"
        };
        var outputPath = Path.Combine(outputDir, $"{baseName}{extension}");
        var content = format.ToLowerInvariant() switch
        {
            "srt" => ToSrt(job),
            "vtt" => ToVtt(job),
            _ => ToTxt(job)
        };

        Directory.CreateDirectory(outputDir);
        File.WriteAllText(outputPath, content, Encoding.UTF8);
        job.OutputPath = outputPath;
    }

    // ── TXT ──────────────────────────────────────────

    public static string ToTxt(AudioFileJob job)
    {
        if (job.SpeakerUtterances is { Count: > 0 })
            return ToDiarizedTxt(job.SpeakerUtterances);

        return job.PlainText ?? "";
    }

    private static string ToDiarizedTxt(List<DiarizedUtterance> utterances)
    {
        var sb = new StringBuilder();
        string? lastSpeaker = null;

        foreach (var u in utterances)
        {
            if (u.SpeakerId != lastSpeaker)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"{u.SpeakerId}:");
                lastSpeaker = u.SpeakerId;
            }
            sb.AppendLine(u.Text);
        }

        return sb.ToString().TrimEnd();
    }

    // ── SRT ──────────────────────────────────────────

    public static string ToSrt(AudioFileJob job)
    {
        if (job.SpeakerUtterances is { Count: > 0 })
            return ToSrtFromUtterances(job.SpeakerUtterances);

        if (job.WordTimings is { Count: > 0 })
            return ToSrtFromWords(job.WordTimings);

        // Fallback: single block from plain text
        return ToSrtSingleBlock(job.PlainText ?? "", 0, job.DurationSeconds);
    }

    private static string ToSrtFromUtterances(List<DiarizedUtterance> utterances)
    {
        var sb = new StringBuilder();
        int index = 1;

        foreach (var u in utterances)
        {
            sb.AppendLine(index.ToString());
            sb.AppendLine($"{FormatSrtTime(u.StartSeconds)} --> {FormatSrtTime(u.EndSeconds)}");
            sb.AppendLine($"[{u.SpeakerId}] {u.Text}");
            sb.AppendLine();
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string ToSrtFromWords(List<WordTiming> words)
    {
        // Group words into ~2-second subtitle blocks
        var sb = new StringBuilder();
        int index = 1;
        const double maxBlockDuration = 2.0;

        int i = 0;
        while (i < words.Count)
        {
            var blockStart = words[i].StartSeconds;
            var blockEnd = blockStart;
            var blockText = new StringBuilder();
            int startIdx = i;

            while (i < words.Count)
            {
                var w = words[i];
                var candidateEnd = w.EndSeconds;
                if (candidateEnd - blockStart > maxBlockDuration && i > startIdx)
                    break;

                blockEnd = candidateEnd;
                if (blockText.Length > 0) blockText.Append(' ');
                blockText.Append(w.Word);
                i++;
            }

            sb.AppendLine(index.ToString());
            sb.AppendLine($"{FormatSrtTime(blockStart)} --> {FormatSrtTime(blockEnd)}");
            sb.AppendLine(blockText.ToString());
            sb.AppendLine();
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string ToSrtSingleBlock(string text, double start, double end)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return $"1\n{FormatSrtTime(start)} --> {FormatSrtTime(end)}\n{text}\n";
    }

    // ── VTT ──────────────────────────────────────────

    public static string ToVtt(AudioFileJob job)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        if (job.SpeakerUtterances is { Count: > 0 })
        {
            foreach (var u in job.SpeakerUtterances)
            {
                sb.AppendLine($"{FormatVttTime(u.StartSeconds)} --> {FormatVttTime(u.EndSeconds)}");
                sb.AppendLine($"<v {u.SpeakerId}>{u.Text}</v>");
                sb.AppendLine();
            }
        }
        else if (job.WordTimings is { Count: > 0 })
        {
            const double maxBlockDuration = 2.0;
            int i = 0;
            while (i < job.WordTimings.Count)
            {
                var blockStart = job.WordTimings[i].StartSeconds;
                var blockEnd = blockStart;
                var blockText = new StringBuilder();
                int startIdx = i;

                while (i < job.WordTimings.Count)
                {
                    var w = job.WordTimings[i];
                    if (w.EndSeconds - blockStart > maxBlockDuration && i > startIdx)
                        break;

                    blockEnd = w.EndSeconds;
                    if (blockText.Length > 0) blockText.Append(' ');
                    blockText.Append(w.Word);
                    i++;
                }

                sb.AppendLine($"{FormatVttTime(blockStart)} --> {FormatVttTime(blockEnd)}");
                sb.AppendLine(blockText.ToString());
                sb.AppendLine();
            }
        }
        else if (!string.IsNullOrWhiteSpace(job.PlainText))
        {
            sb.AppendLine($"{FormatVttTime(0)} --> {FormatVttTime(job.DurationSeconds)}");
            sb.AppendLine(job.PlainText);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ── Time formatting helpers ──────────────────────

    internal static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    internal static string FormatVttTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    /// <summary>Format duration in seconds to "M:SS" or "H:MM:SS" string.</summary>
    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }
}
