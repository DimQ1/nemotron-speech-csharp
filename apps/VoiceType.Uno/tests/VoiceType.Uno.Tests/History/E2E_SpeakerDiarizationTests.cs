using System.Diagnostics;
using System.Globalization;
using SpeechLib.AudioStorage;
using SpeechLib.Diarization;
using SpeechLib.ParakeetTdt;
using VoiceType.Uno.History;
using VoiceType.Uno.Services.Jobs;
using Xunit;
using Xunit.Abstractions;

namespace VoiceType.Uno.Tests.History;

/// <summary>
/// Speaker detection end to end with the real models: a synthetic conversation (wav + RTTM) is stored as a recording
/// (Opus 48 kbps through <see cref="RecordingStore"/>), transcribed by Parakeet TDT INT4 (exact decoder word times) through
/// <see cref="OfflineTranscriber"/>, and the <c>diarize</c> job runs through the real <see cref="JobRunner"/> with the ONNX
/// model. Prints the share of words whose speaker is right after the best mapping of detected people to RTTM speakers, the
/// detected speaker count and the real-time factors. Temp folders only: the user's data is never touched.
/// <para>
/// Opt-in (it loads two models and takes minutes): set <c>VT_DIAR_E2E=1</c>. Needs (override with env vars): <c>VT_DIAR_MODELS_ROOT</c> (folder holding <c>nemotron-3-diarization-onnx</c>),
/// <c>VT_PARAKEET_INT4</c> (Parakeet TDT INT4 folder), <c>VT_DIAR_TESTSET</c> (conv*.wav + conv*.rttm),
/// <c>VT_DIAR_E2E_OUT</c> (results file folder), <c>VT_DIAR_E2E_ONLY</c> (comma separated name parts). Without the switch or the files the test
/// returns at once.
/// </para>
/// </summary>
public class E2E_SpeakerDiarizationTests(ITestOutputHelper output)
{
    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static readonly string ModelsRoot = Env("VT_DIAR_MODELS_ROOT", @"E:\Learn\nemotron-speech-csharp\models");
    private static readonly string Parakeet = Env("VT_PARAKEET_INT4", @"E:\Learn\nemotron-speech-csharp\models\asr\parakeet-tdt\onnx\int4");
    private static readonly string TestSet = Env("VT_DIAR_TESTSET",
        @"C:\Users\DZMITR~1.SHC\AppData\Local\Temp\claude\e--Learn-nemotron-speech-csharp\cb40b311-c71f-47dc-9878-f199bc87a142\scratchpad\diar\testset");

    private sealed record Ref(double Start, double End, string Speaker);

    private sealed class Device : IDeviceConditions
    {
        public bool HasBattery => false;
        public bool IsCharging => true;
        public event Action? Changed { add { } remove { } }
        public void Dispose() { }
    }

    private static List<Ref> ReadRttm(string path) =>
        File.ReadAllLines(path)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length > 7 && p[0] == "SPEAKER")
            .Select(p => new Ref(double.Parse(p[3], CultureInfo.InvariantCulture),
                double.Parse(p[3], CultureInfo.InvariantCulture) + double.Parse(p[4], CultureInfo.InvariantCulture), p[7]))
            .ToList();

    /// <summary>
    /// Share of words with the right speaker after the best one-to-one mapping of detected people to reference speakers.
    /// Only words that lie inside exactly one reference turn count (silence and overlap are skipped).
    /// </summary>
    private static (int Evaluated, int Correct, int Skipped) WordAccuracy(
        IReadOnlyList<(double Mid, string? Person)> words, IReadOnlyList<Ref> reference)
    {
        var speakers = reference.Select(r => r.Speaker).Distinct().Order().ToList();
        var people = words.Select(w => w.Person).Where(p => p is not null).Distinct().Order().ToList();
        var counts = new double[Math.Max(1, people.Count), Math.Max(1, speakers.Count)];
        var evaluated = new List<(string? Person, string Speaker)>();
        var skipped = 0;
        foreach (var (mid, person) in words)
        {
            var active = reference.Where(r => mid >= r.Start && mid < r.End).Select(r => r.Speaker).Distinct().ToList();
            if (active.Count != 1)
            {
                skipped++;
                continue;
            }
            evaluated.Add((person, active[0]));
            if (person is not null)
                counts[people.IndexOf(person), speakers.IndexOf(active[0])]++;
        }

        var assignment = HungarianAssignment.Maximize(counts);
        var map = new Dictionary<string, string>();
        for (var i = 0; i < people.Count; i++)
        {
            if (assignment[i] is var j and >= 0 && j < speakers.Count)
                map[people[i]!] = speakers[j];
        }
        var correct = evaluated.Count(e => e.Person is not null && map.TryGetValue(e.Person, out var s) && s == e.Speaker);
        return (evaluated.Count, correct, skipped);
    }

    [Fact]
    public async Task Diarize_ShouldLabelWordsOfSyntheticConversationsWithTheRightSpeaker()
    {
        if (Environment.GetEnvironmentVariable("VT_DIAR_E2E") != "1")
        {
            output.WriteLine("skipped: set VT_DIAR_E2E=1 to run the real-model end-to-end test");
            return;
        }
        var missing = new[] { Path.Combine(ModelsRoot, DiarizationModelLocator.FolderName), Parakeet, TestSet }
            .Where(p => !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            output.WriteLine("skipped, missing: " + string.Join("; ", missing));
            return;
        }

        var only = Env("VT_DIAR_E2E_ONLY", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = Directory.GetFiles(TestSet, "conv*.wav").Order()
            .Where(f => only.Length == 0 || only.Any(o => Path.GetFileName(f).Contains(o, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.NotEmpty(files);

        var outDir = Env("VT_DIAR_E2E_OUT", Path.Combine(Path.GetTempPath(), "voicetype-diar-e2e"));
        Directory.CreateDirectory(outDir);
        var resultsFile = Path.Combine(outDir, "e2e-results.txt");
        File.WriteAllText(resultsFile, "");
        void Say(string line)
        {
            output.WriteLine(line);
            File.AppendAllText(resultsFile, line + Environment.NewLine);
        }

        using var temp = new TempDirectory();
        var store = new RecordingStore(Path.Combine(temp.Path, "sessions"));
        var queue = new JobQueue(Path.Combine(temp.Path, "jobs", "queue.json"));
        var settings = new HistorySettings();
        var factory = new OnnxDiarizerFactory(() => [ModelsRoot]);
        var speakers = new SpeakerService(store, queue, factory, () => settings);
        Assert.True(speakers.IsModelAvailable, speakers.ModelStatus);
        Say("model: " + speakers.ModelStatus);

        await using var runner = new JobRunner(queue, [new DiarizeJob(store, factory, () => settings)], new Device(),
            new RecordingActivity(), () => settings, options: new JobRunnerOptions { PollInterval = TimeSpan.FromMilliseconds(200) });
        runner.Start();

        var totalEvaluated = 0;
        var totalCorrect = 0;
        var totalEstimatedCorrect = 0;
        var exactCount = 0;
        double audioSeconds = 0, diarizeSeconds = 0, asrSeconds = 0;
        var perFile = new List<double>();

        foreach (var wav in files)
        {
            var name = Path.GetFileNameWithoutExtension(wav);
            var reference = ReadRttm(Path.ChangeExtension(wav, ".rttm"));
            var refSpeakers = reference.Select(r => r.Speaker).Distinct().Count();
            var original = RecordingAudioReader.ReadMono(wav, 16000);
            var seconds = original.Length / 16000.0;

            // 1. Store the recording the way the app does: Opus 48 kbps.
            var entry = store.CreateRecording(DateTimeOffset.Now);
            var audioBase = Path.Combine(entry.FolderPath, "audio");
            await using (var writer = RecordingAudioWriterFactory.Create(audioBase, RecordingAudioFormat.Standard, 16000, queued: false))
            {
                for (var i = 0; i < original.Length; i += 16000)
                    writer.Append(original.AsSpan(i, Math.Min(16000, original.Length - i)));
                var stored = await writer.CompleteAsync();
                store.FinishRecording(entry.Id, DateTimeOffset.Now, new AudioInfo
                {
                    File = Path.GetFileName(stored.Path), Codec = "opus", SampleRate = 16000, Bitrate = 48000,
                    DurationSec = stored.DurationSeconds, Bytes = stored.Bytes,
                });
            }

            // 2. Parakeet transcript with exact decoder word times, from the stored (decoded) audio, like a re-recognition.
            var decoded = RecordingAudioReader.ReadMono(store.GetAudioPath(entry.Id)!, 16000);
            var asr = Stopwatch.StartNew();
            OfflineTranscriptionResult transcript;
            using (var recognizer = new ParakeetTdtRecognizer(Parakeet, chunkSeconds: OfflineRecognitionJobChunk))
                transcript = OfflineTranscriber.Run(recognizer, decoded);
            asr.Stop();
            asrSeconds += asr.Elapsed.TotalSeconds;
            var spec = HistoryFixtures.LiveSpec("parakeet-int4");
            store.AddVersion(entry.Id, spec, transcript.Document);
            var decoderSegments = transcript.Document.Segments.Count(s => s.WordTiming == WordTimingKinds.Decoder);

            // 3. The Diarize job through the real runner and the real model.
            var jobId = speakers.EnqueueDiarize(entry.Id, "v1", runNow: true);
            Assert.NotNull(jobId);
            var job = Stopwatch.StartNew();
            while (!queue.Get(jobId!)!.IsFinished && job.Elapsed < TimeSpan.FromMinutes(10))
                await Task.Delay(100);
            job.Stop();
            var finished = queue.Get(jobId!)!;
            Assert.True(finished.State == JobState.Completed, $"{name}: {finished.State} {finished.Error}");

            var manifest = store.Get(entry.Id)!.Manifest;
            var run = manifest.DiarizationRuns.Single();
            var view = speakers.GetLayer(entry.Id, "v1")!;
            var layerInfo = manifest.CurrentSpeakerLayer("v1")!;

            // 4. Word-level accuracy against the RTTM.
            var doc = store.LoadTranscript(entry.Id, "v1");
            var words = new List<(double Mid, string? Person)>();
            foreach (var segment in doc.Segments)
            {
                var labels = view.Segments.Single(s => s.SegmentIndex == segment.Index).WordPersonIds;
                var textWords = SplitWords(segment.Text);
                var timed = segment.Words.Count == textWords.Count ? segment.Words : [];
                for (var w = 0; w < timed.Count && w < labels.Count; w++)
                    words.Add(((timed[w].Start + timed[w].End) / 2, labels[w]));
            }
            var (evaluated, correct, skipped) = WordAccuracy(words, reference);

            // The same run with estimated word times (what a live or non-Parakeet transcript would get).
            var estimatedDoc = store.LoadTranscript(entry.Id, "v1");
            foreach (var segment in estimatedDoc.Segments)
            {
                segment.Words = TranscriptTimings.Distribute(SplitWords(segment.Text), segment.Start, segment.End);
                segment.WordTiming = WordTimingKinds.Estimated;
            }
            var estimated = SpeakerLayerBuilder.Build(store.LoadDiarizationRun(entry.Id, run.Id), estimatedDoc, "v1");
            var estimatedView = SpeakerViews.Build(manifest, layerInfo, estimated.Document, null);
            var estimatedWords = new List<(double Mid, string? Person)>();
            foreach (var segment in estimatedDoc.Segments)
            {
                var labels = estimatedView.Segments.Single(s => s.SegmentIndex == segment.Index).WordPersonIds;
                for (var w = 0; w < segment.Words.Count && w < labels.Count; w++)
                    estimatedWords.Add(((segment.Words[w].Start + segment.Words[w].End) / 2, labels[w]));
            }
            var (_, estimatedCorrect, _) = WordAccuracy(estimatedWords, reference);

            var accuracy = evaluated == 0 ? 0 : 100.0 * correct / evaluated;
            Say($"{name,-18} {seconds,6:F1}s  speakers ref/found {refSpeakers}/{view.People.Count}  words {evaluated} (+{skipped} skipped, " +
                $"{decoderSegments}/{doc.Segments.Count} decoder segments)  accuracy {accuracy:F1}%  estimated-times {(evaluated == 0 ? 0 : 100.0 * estimatedCorrect / evaluated):F1}%  " +
                $"diar RTF {run.Stats!.Rtf:F3} ({run.Stats.ElapsedSec:F1}s)  job {job.Elapsed.TotalSeconds:F1}s  asr {asr.Elapsed.TotalSeconds:F1}s  layer timing {layerInfo.WordTiming}");

            totalEvaluated += evaluated;
            totalCorrect += correct;
            totalEstimatedCorrect += estimatedCorrect;
            exactCount += view.People.Count == refSpeakers ? 1 : 0;
            audioSeconds += seconds;
            diarizeSeconds += run.Stats.ElapsedSec ?? 0;
            perFile.Add(accuracy);

            // The job leaves the model unloaded and the people editable.
            Assert.Equal(1, manifest.DiarizationRuns.Count);
            Assert.Equal(WordTimingKinds.Decoder, layerInfo.WordTiming);
        }

        await runner.StopAsync();
        var overall = totalEvaluated == 0 ? 0 : 100.0 * totalCorrect / totalEvaluated;
        Say($"TOTAL {files.Count} files, {audioSeconds:F0}s audio, {totalEvaluated} words: word-level speaker accuracy {overall:F1}% " +
            $"(per-file mean {perFile.Average():F1}%, min {perFile.Min():F1}%), estimated word times {(100.0 * totalEstimatedCorrect / Math.Max(1, totalEvaluated)):F1}%, " +
            $"speaker count exact {exactCount}/{files.Count}, diarization RTF {diarizeSeconds / audioSeconds:F3}, Parakeet INT4 RTF {asrSeconds / audioSeconds:F3}");
        Assert.True(overall > 70, $"word-level speaker accuracy {overall:F1}% is far below what the model does on this set");
    }

    private const double OfflineRecognitionJobChunk = 1.0;

    private static IReadOnlyList<string> SplitWords(string text) => TextTokens.SplitWords(text);
}
