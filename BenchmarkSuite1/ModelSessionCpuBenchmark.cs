using BenchmarkDotNet.Attributes;
using CommonUtils;
using SpeechLib.Audio;
using System;
using System.IO;
using System.Text;
using Microsoft.VSDiagnostics;

namespace NemotronSpeech;
[CPUUsageDiagnoser]
public class ModelSessionCpuBenchmark
{
    private ModelSession? _session;
    private float[][] _chunks = [];
    [Params("cpu", "cpu-int8", "cpu-int4")]
    public string ModelVariant { get; set; } = "cpu";

    [Params(1, 2)]
    public int NumBeams { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        using var probe = CreateSession();
        var repoRoot = FindRepoRoot();
        var audioPath = Path.Combine(repoRoot, "Test-Audio", "sample-0.mp3");
        var samples = AudioUtils.LoadFile(audioPath, probe.SampleRate);
        _chunks = SplitIntoChunks(samples, probe.ChunkSamples);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _session = CreateSession();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _session?.Dispose();
        _session = null;
    }

    [Benchmark]
    public int TranscribeSample()
    {
        ArgumentNullException.ThrowIfNull(_session);
        var text = new StringBuilder();
        foreach (var chunk in _chunks)
        {
            var part = ((SpeechLib.IStreamingSpeechRecognizer)_session).ProcessAudio(chunk);
            if (!string.IsNullOrEmpty(part))
                text.Append(part);
        }

        var final = ((SpeechLib.IStreamingSpeechRecognizer)_session).Flush();
        if (!string.IsNullOrEmpty(final))
            text.Append(final);
        return text.Length;
    }

    private ModelSession CreateSession()
    {
        var repoRoot = FindRepoRoot();
        var modelPath = Path.Combine(repoRoot, "models-onnx", ModelVariant);
        var searchOptions = new GeneratorParamsArgs
        {
            num_beams = NumBeams,
            do_sample = false,
            repetition_penalty = 1.1
        };
        return new ModelSession(modelPath, "cpu", null, useVad: false, searchOptions);
    }

    private static float[][] SplitIntoChunks(float[] samples, int chunkSize)
    {
        if (samples.Length == 0)
            return[new float[chunkSize]];
        var chunkCount = (samples.Length + chunkSize - 1) / chunkSize;
        var chunks = new float[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "models-onnx")) && Directory.Exists(Path.Combine(dir.FullName, "Test-Audio")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing models-onnx and Test-Audio was not found.");
    }
}