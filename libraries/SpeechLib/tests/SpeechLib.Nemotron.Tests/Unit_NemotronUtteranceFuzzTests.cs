using System.Text.RegularExpressions;
using SpeechLib.Models;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// Randomised streams through the utterance layer over the fake engine: scripted tokens in random frames (words, late word pieces,
/// closing marks and tags, long pauses), random batching down to one sample, mid-stream flushes, resets, silence-dropped chunks, the
/// length cap, the gain and <c>TranscribeWords</c>. Whatever the stream does, every Final has one timed word per whitespace word of
/// its text, and the words of the whole stream are ordered, have no negative length and never overlap, also across utterances.
/// </summary>
public sealed class Unit_NemotronUtteranceFuzzTests
{
    private static readonly Regex Tag = new(@"<\w{2,3}(-\w{2,4})?>", RegexOptions.Compiled);
    private static readonly string[] Pieces = ["▁the", "▁there", "▁and", "▁has", "▁be", "▁is", "▁we", "▁вы"];
    private static readonly int[] Batches = [7, 160, 800, 4000, 8960, 8967, 3 * 8960];

    private static readonly Lazy<NemotronVocabulary> Vocabulary = new(() =>
        NemotronVocabulary.FromPieces(NemotronTestEnvironment.ReadVocabularyLines(), 13087, 13088));

    private static readonly Lazy<string[]> VocabularyLines = new(NemotronTestEnvironment.ReadVocabularyLines);

    private static int Id(string piece) => Array.IndexOf(VocabularyLines.Value, piece);

    private static int WhitespaceWords(string text) =>
        Tag.Replace(text, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Room tone with speech-like bursts, so that the energy history has a floor and a speech level.</summary>
    private static float[] Audio(double seconds, int seed)
    {
        var random = new Random(seed);
        var audio = new float[(int)seconds * 16000];
        for (var i = 0; i < audio.Length; i++)
        {
            var burst = i / 16000 % 3 < 2 && i / 16000 > 1;
            audio[i] = (float)((random.NextDouble() - 0.5) * (burst ? 0.2 : 0.002));
        }
        return audio;
    }

    private static List<(int Frame, string Piece)> Stream(Random random, int length)
    {
        var tokens = new List<(int, string)>();
        var frame = random.Next(10);
        for (var i = 0; i < length; i++)
        {
            tokens.Add((frame, Pieces[random.Next(Pieces.Length)]));
            if (random.Next(7) == 0)
                tokens.Add((frame + random.Next(2), "s"));
            if (random.Next(6) == 0)
            {
                tokens.Add((frame + 1, random.Next(3) == 0 ? "?" : "."));
                if (random.Next(3) > 0)
                {
                    tokens.Add((frame + 1, "▁"));
                    tokens.Add((frame + 1, random.Next(4) == 0 ? "<ru-RU>" : "<en-US>"));
                }
            }
            frame += 1 + (random.Next(5) == 0 ? random.Next(30) : random.Next(4));
        }
        return tokens.Select((t, i) => (t, i)).OrderBy(x => x.t.Item1).ThenBy(x => x.i).Select(x => x.t).ToList();
    }

    [Fact]
    public void RandomStreams_ShouldKeepOneTimedWordPerWordAndNeverOverlapWords()
    {
        var finals = 0;
        for (var run = 0; run < 150; run++)
        {
            var random = new Random(5000 + run);
            var c112 = random.Next(2) == 0;
            int chunk = c112 ? 17920 : 8960, framesPerChunk = c112 ? 14 : 7;
            var tokens = Stream(random, 80).Select(t => (t.Frame, Id: Id(t.Piece))).ToList();
            var options = new NemotronUtteranceOptions
            {
                KeepLanguageTagInFinal = random.Next(2) == 0,
                MaxUtteranceSeconds = random.Next(3) == 0 ? 3 : 30,
                StopHistoryEouSeconds = new[] { 0.5, 1.04, 2.0 }[random.Next(3)],
                AutoGain = random.Next(2) == 0,
            };
            var engine = new FakeChunkEngine(chunk, framesPerChunk)
            {
                Script = (c, _) => tokens.Where(t => t.Frame / framesPerChunk == c).Select(t => (t.Id, t.Frame % framesPerChunk)),
            };
            var inner = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig(c112 ? "int4-c112" : "int4-c056"), Vocabulary.Value,
                () => new FakeGate(chunk, index => index % 5 != 3), random.Next(3) == 0);
            using var recognizer = new NemotronUtteranceRecognizer(inner, options);

            var limit = (int)((tokens[^1].Frame + 40) * 0.08 * 16000);
            var audio = Audio(limit / 16000 + 2, run);
            var previous = default(WordTiming);
            var hasPrevious = false;

            void Check(StreamingResult result, string context)
            {
                if (result.Final is null)
                    return;
                finals++;
                Assert.True(WhitespaceWords(result.Final) == result.FinalWords!.Count, $"run {run} {context}: '{result.Final}' vs {result.FinalWords.Count} words");
                foreach (var word in result.FinalWords)
                {
                    Assert.True(word.StartSeconds >= 0 && word.EndSeconds >= word.StartSeconds, $"run {run} {context}: '{word.Word}' {word.StartSeconds}-{word.EndSeconds}");
                    if (hasPrevious)
                    {
                        Assert.True(word.StartSeconds >= previous.StartSeconds - 1e-9, $"run {run} {context}: '{word.Word}'@{word.StartSeconds:F3} starts before '{previous.Word}'@{previous.StartSeconds:F3}-{previous.EndSeconds:F3}; Final '{result.Final}' words [{string.Join(" | ", result.FinalWords!.Select(w => $"{w.Word}@{w.StartSeconds:F3}-{w.EndSeconds:F3}"))}] cont={result.FinalContinuesPrevious} eou={options.StopHistoryEouSeconds} cap={options.MaxUtteranceSeconds} c112={c112}");
                        Assert.True(word.StartSeconds >= previous.EndSeconds - 1e-9,
                            $"run {run} {context}: '{word.Word}'@{word.StartSeconds:F3} overlaps '{previous.Word}'@{previous.StartSeconds:F3}-{previous.EndSeconds:F3}");
                    }
                    previous = word;
                    hasPrevious = true;
                }
            }

            for (var fed = 0; fed < limit;)
            {
                var op = random.Next(100);
                if (op < 80)
                {
                    var count = Math.Min(limit - fed, Batches[random.Next(Batches.Length)]);
                    var piece = audio[fed..(fed + count)];
                    if (random.Next(4) == 0)
                        recognizer.ProcessAudio(piece);                  // the plain interface moves the same bookkeeping on, its Finals are not reported
                    else
                        Check(recognizer.ProcessUtterance(piece), "stream");
                    fed += count;
                }
                else if (op < 90)
                {
                    Check(recognizer.FlushUtterance(), "flush");
                }
                else if (op < 95)
                {
                    recognizer.ResetStreamingState();
                    hasPrevious = false;
                }
                else
                {
                    // without a second engine TranscribeWords resets the stream before and after it
                    recognizer.TranscribeWords(audio[Math.Max(0, fed - 5 * chunk)..fed]);
                    hasPrevious = false;
                }
            }
            Check(recognizer.FlushUtterance(), "end");
        }

        Assert.True(finals > 500, $"only {finals} Finals: the streams did not exercise the layer");
    }
}
