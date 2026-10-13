using SpeechLib.Diarization;
using SpeechLib.ModelDownload;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using Xunit;
using Xunit.Abstractions;

namespace VoiceType.Uno.Tests;

/// <summary>
/// Opt-in (<c>VT_DIAR_DOWNLOAD=1</c>, about 106 MB from huggingface.co/DimQ1/nemotron-3-diarization-onnx): downloads the INT8
/// model through the real download manager into a temp models root, validates it, finds it with the locator and runs a tiny
/// diarization on 10 s of synthetic audio.
/// </summary>
public sealed class E2E_DiarizationModelDownloadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Int8_ShouldDownloadFromTheRepoSubfolder_Validate_AndRun()
    {
        if (Environment.GetEnvironmentVariable("VT_DIAR_DOWNLOAD") != "1")
        {
            output.WriteLine("skipped: set VT_DIAR_DOWNLOAD=1 to download the 106 MB model");
            return;
        }

        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(Path.Combine(temp.Path, "models"));
        using var downloads = new ModelDownloadManager(new HuggingFaceClient(new HttpHubTransport(HuggingFaceClient.CreateDefaultHttpClient())));
        using var service = new DiarizationModelService(downloads, () => layout);
        var model = DiarizationModelCatalog.All[0];

        service.StartDownload(model);
        var job = downloads.FindActive(DiarizationModelCatalog.DownloadKey(model));
        Assert.NotNull(job);
        var folder = await job!.Completion.WaitAsync(TimeSpan.FromMinutes(10));
        output.WriteLine($"downloaded to {folder}: {DownloadProgressFormatter.FormatBytes(ModelStorage.SizeOf(folder))}");

        // Files of the sub-folder only, sizes as published.
        Assert.Equal(layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName), folder);
        Assert.Equal(104_653_656, new FileInfo(Path.Combine(folder, DiarizationModelLocator.Int8StepFileName)).Length);
        Assert.False(File.Exists(Path.Combine(folder, "README.md")));
        Assert.False(File.Exists(Path.Combine(folder, DiarizationModelLocator.Fp32StepFileName)));
        Assert.Empty(Directory.GetFiles(folder, "*.download"));

        var descriptor = DiarizationModelLocator.Validate(folder, DiarizationPrecision.Int8);
        Assert.Equal(DiarizationPrecision.Int8, descriptor.Precision);
        Assert.Equal(DiarizationModelState.Active, service.StateOf(model));
        Assert.True(service.IsModelAvailable);
        Assert.NotNull(DiarizationModelLocator.Find(layout.DiarizationScanRoots));

        // The job factory finds it under <root>/diarization and a tiny diarization runs.
        var factory = new OnnxDiarizerFactory(() => layout.DiarizationScanRoots);
        var found = factory.Find();
        Assert.NotNull(found);
        Assert.Equal("Int8", found!.Precision);

        using var diarizer = factory.Create(found, new DiarizerRunOptions("offline", 2, null, new PostProcessingOptions()));
        var seconds = 10;
        var samples = new float[16_000 * seconds];
        var random = new Random(7);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 180 * i / 16_000.0) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * i / 16_000.0)) + 0.01 * (random.NextDouble() - 0.5));
        var result = diarizer.Diarize([samples], seconds, null, CancellationToken.None);

        output.WriteLine($"diarized {result.DurationSeconds:0.0}s in {result.ElapsedSeconds:0.00}s, {result.Turns.Count} turns");
        Assert.Equal(seconds, result.DurationSeconds, 1);
    }
}
