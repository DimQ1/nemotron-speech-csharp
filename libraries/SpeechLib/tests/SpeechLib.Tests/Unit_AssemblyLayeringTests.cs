using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SpeechLib.ModelFormats;
using SpeechLib.Providers;
using Xunit;

namespace SpeechLib.Tests;

/// <summary>
/// Guards the project layering: these rules were violations before the refactor and
/// nothing in a normal build would notice them creeping back.
/// </summary>
/// <remarks>
/// Scope: these tests see only the assemblies that reach the output of <c>SpeechLib.Tests</c> (the <c>SpeechLib*.dll</c> files:
/// core, providers, model download, diarization and what they bring). The apps (CLI, WinUI, Uno) have their own packages and
/// output, which only <c>tools/scripts/check-no-genai.ps1</c> looks at (restore state, <c>dotnet list package</c>, build output of the three heads).
/// </remarks>
public sealed class Unit_AssemblyLayeringTests
{
    private static string[] ReferencedAssemblyNames(Type probe) =>
        probe.Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .ToArray();

    /// <summary>
    /// The inference layer must not depend on the downloader: it used to reference
    /// SpeechLib.ModelDownload only for the model-format detectors.
    /// </summary>
    [Fact]
    public void Providers_DoNotReferenceModelDownload()
    {
        var referenced = ReferencedAssemblyNames(typeof(RecognizerFactory));

        // Sanity: the probe must actually see the references this assembly does have,
        // otherwise the assertions below could never fail.
        Assert.Contains("SpeechLib", referenced);
        Assert.DoesNotContain("SpeechLib.ModelDownload", referenced);
    }

    /// <summary>
    /// System.CommandLine was pulled in for the CLI parsing that lived in the deleted
    /// Common.cs; a library must not carry a CLI parser for code without callers.
    /// </summary>
    [Fact]
    public void Providers_DoNotReferenceCommandLineParser()
    {
        var referenced = ReferencedAssemblyNames(typeof(RecognizerFactory));

        Assert.DoesNotContain(referenced, name => name.StartsWith("System.CommandLine", StringComparison.Ordinal));
    }

    /// <summary>The portable contract layer stays free of every model-specific runtime package.</summary>
    [Fact]
    public void Core_DoesNotReferenceGenAi()
    {
        var core = typeof(IAudioRecorder).Assembly;
        var referenced = ReferencedAssemblyNames(typeof(IAudioRecorder));

        // Sanity, as in the sibling tests: the probe must see this assembly and the references it really has (the plain
        // ONNX Runtime, which the core uses for its session tuning), otherwise the assertion below could never fail.
        Assert.Equal("SpeechLib", core.GetName().Name);
        Assert.NotEmpty(referenced);
        Assert.Contains("Microsoft.ML.OnnxRuntime", referenced);
        Assert.DoesNotContain(referenced, name => name.Contains("OnnxRuntimeGenAI", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Model-format detection needs file inspection only, so both apps and the providers
    /// resolve it from the core assembly instead of the downloader.
    /// </summary>
    [Fact]
    public void ModelFormatDetectors_LiveInCoreAssembly()
    {
        var core = typeof(IAudioRecorder).Assembly;

        Assert.Same(core, typeof(ModelFolderScanner).Assembly);
        Assert.Same(core, typeof(ParakeetModelDetector).Assembly);
        Assert.Same(core, typeof(Qwen3ModelDetector).Assembly);
        Assert.Same(core, typeof(VibeVoiceModelDetector).Assembly);
    }

    /// <summary>
    /// ONNX Runtime GenAI was removed: every recognizer runs on plain ONNX Runtime. No assembly of the product (the
    /// <c>SpeechLib*.dll</c> next to the tests: core, providers, diarization, downloader, ...) may reference a GenAI
    /// assembly again. The references are read from the metadata of the files, so no assembly has to be loaded.
    /// </summary>
    [Fact]
    public void ProductAssemblies_DoNotReferenceOnnxRuntimeGenAi()
    {
        var productAssemblies = Directory.GetFiles(AppContext.BaseDirectory, "SpeechLib*.dll");

        // Sanity: the probe must see the assemblies that matter, otherwise the assertion below could never fail.
        Assert.Contains(productAssemblies, path => Path.GetFileName(path) == "SpeechLib.dll");
        Assert.Contains(productAssemblies, path => Path.GetFileName(path) == "SpeechLib.Providers.dll");
        Assert.Contains(productAssemblies, path => Path.GetFileName(path) == "SpeechLib.Diarization.dll");

        foreach (var path in productAssemblies)
            Assert.DoesNotContain(ReferencedAssemblyNames(path), name => name.Contains("OnnxRuntimeGenAI", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// No ONNX Runtime GenAI binary, managed (<c>Microsoft.ML.OnnxRuntimeGenAI.dll</c>) or native
    /// (<c>onnxruntime-genai.dll</c>, <c>libonnxruntime-genai.so</c>, ...), reaches the output of a project that uses the product.
    /// </summary>
    [Fact]
    public void BuildOutput_HasNoOnnxRuntimeGenAiBinary()
    {
        var genAiFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("onnxruntime-genai", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).Contains("OnnxRuntimeGenAI", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(genAiFiles);
        Assert.NotEmpty(Directory.GetFiles(AppContext.BaseDirectory, "Microsoft.ML.OnnxRuntime.dll")); // the plain runtime is what is there
    }

    private static string[] ReferencedAssemblyNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            return [];

        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name)).ToArray();
    }
}
