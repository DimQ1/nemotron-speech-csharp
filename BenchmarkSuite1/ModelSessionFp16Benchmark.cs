using BenchmarkDotNet.Attributes;
using Microsoft.VSDiagnostics;
using SpeechLib;
using SpeechLib.Audio;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NemotronSpeech;

[CPUUsageDiagnoser]
[MemoryDiagnoser]
public class ModelSessionFp16Benchmark
{
    private ModelSession? _session;
    private float[][] _chunks = [];
    private double _audioDurationSeconds;
    private double _lastElapsedSeconds;
    private double _lastRtf;
    private string _lastTranscript = string.Empty;

    [Params(1, 2)]
    public int NumBeams { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        using var probe = CreateSession();
        var repoRoot = FindRepoRoot();
        var audioPath = Path.Combine(repoRoot, "Test-Audio", "sample-0.mp3");
        var samples = AudioUtils.LoadFile(audioPath, probe.SampleRate);
        _audioDurationSeconds = (double)samples.Length / probe.SampleRate;
        _chunks = SplitIntoChunks(samples, probe.ChunkSamples);
    }

    [IterationSetup]
    public void IterationSetup() => _session = CreateSession();

    [IterationCleanup]
    public void IterationCleanup()
    {
        _session?.Dispose();
        _session = null;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        var resultsDirectory = Path.Combine(FindRepoRoot(), "build", "benchmark-results");
        Directory.CreateDirectory(resultsDirectory);
        var resultPath = Path.Combine(resultsDirectory, $"fp16-cuda-beams{NumBeams}.txt");
        File.WriteAllText(
            resultPath,
            $"model_variant=fp16{Environment.NewLine}" +
            $"execution_provider=cuda{Environment.NewLine}" +
            $"num_beams={NumBeams}{Environment.NewLine}" +
            $"audio_duration_seconds={_audioDurationSeconds:F6}{Environment.NewLine}" +
            $"elapsed_seconds={_lastElapsedSeconds:F6}{Environment.NewLine}" +
            $"rtf={_lastRtf:F6}{Environment.NewLine}" +
            $"transcript={_lastTranscript}{Environment.NewLine}");
    }

    [Benchmark]
    public double TranscribeSampleRtf()
    {
        ArgumentNullException.ThrowIfNull(_session);
        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();
        foreach (var chunk in _chunks)
        {
            var part = ((IStreamingSpeechRecognizer)_session).ProcessAudio(chunk);
            if (!string.IsNullOrEmpty(part))
                text.Append(part);
        }

        var final = ((IStreamingSpeechRecognizer)_session).Flush();
        if (!string.IsNullOrEmpty(final))
            text.Append(final);

        stopwatch.Stop();
        _lastElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        _lastRtf = _lastElapsedSeconds / _audioDurationSeconds;
        _lastTranscript = text.ToString();
        return _lastRtf;
    }

    private ModelSession CreateSession()
    {
        var modelPath = Path.Combine(FindRepoRoot(), "converter", "src", "build", "onnx_models_fp16_cuda");
        var searchOptions = new GeneratorParamsArgs
        {
            num_beams = NumBeams,
            do_sample = false,
            repetition_penalty = 1.1
        };
        return new ModelSession(modelPath, "cuda", null, useVad: false, searchOptions);
    }

    private static float[][] SplitIntoChunks(float[] samples, int chunkSize)
    {
        if (samples.Length == 0)
            return [new float[chunkSize]];

        var chunkCount = (samples.Length + chunkSize - 1) / chunkSize;
        var chunks = new float[chunkCount][];
        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = new float[chunkSize];
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, samples.Length - offset);
            Array.Copy(samples, offset, chunk, 0, length);
            chunks[i] = chunk;
        }

        return chunks;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "models-onnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Test-Audio")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing models-onnx/ and Test-Audio was not found.");
    }
}