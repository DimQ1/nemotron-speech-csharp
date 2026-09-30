using SpeechLib.ModelFormats;
using SpeechLib.Providers;
using Xunit;

namespace SpeechLib.Tests;

/// <summary>
/// Guards the project layering: these rules were violations before the refactor and
/// nothing in a normal build would notice them creeping back.
/// </summary>
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

    /// <summary>
    /// The portable contract layer stays free of the model-specific runtime: ONNX Runtime
    /// GenAI lives in SpeechLib.Providers, and the GenAI sources are compiled there too.
    /// </summary>
    [Fact]
    public void Core_DoesNotReferenceGenAi()
    {
        var referenced = ReferencedAssemblyNames(typeof(IAudioRecorder));

        Assert.DoesNotContain(referenced, name => name.Contains("OnnxRuntimeGenAI", StringComparison.Ordinal));
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

    /// <summary>ModelSession drives the GenAI runtime, so it must be compiled into the provider assembly.</summary>
    [Fact]
    public void ModelSession_LivesInProviderAssembly()
    {
        Assert.Same(typeof(RecognizerFactory).Assembly, typeof(ModelSession).Assembly);
    }
}
