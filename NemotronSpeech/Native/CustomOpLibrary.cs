using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace NemotronSpeech.Native;

/// <summary>
/// Registers the native custom-op library (<c>nemotron_swish_cpu.dll</c>) that
/// supplies the missing CPU kernel for the ONNX opset-24 <c>Swish</c> operator.
///
/// ORT 1.25 parses opset-24 graphs but ships no Swish-24 CPU kernel, so models
/// exported with <c>target_opset: 24</c> fail with "Missing Kernel" on CPU.
/// The native library (see <c>Native/swish_cpu.cpp</c>) registers a kernel that
/// overrides the standard <c>ai.onnx</c> domain.
///
/// Registration is process-wide (ORT keeps the domain after the temporary
/// session is disposed) and is only performed when the model's
/// <c>genai_config.json</c> requests it via
/// <c>model.session.session_options.use_swish_custom_op</c> or when the
/// encoder graph actually contains Swish nodes from the ai.onnx domain.
/// </summary>
public static class CustomOpLibrary
{
    private const string LibraryFileName = "nemotron_swish_cpu.dll";

    // 0 = not attempted yet, 1 = registered, -1 = failed (log once, never retry).
    private static int _registrationState;

    /// <summary>
    /// Registers the Swish custom-op library if the model at <paramref name="modelPath"/>
    /// needs it. Safe to call multiple times — only the first call has an effect.
    /// </summary>
    public static void RegisterIfNeeded(string modelPath)
    {
        if (Volatile.Read(ref _registrationState) != 0)
            return;

        if (!ModelNeedsSwishKernel(modelPath))
            return;

        if (Interlocked.CompareExchange(ref _registrationState, 1, 0) != 0)
            return; // Another thread won the race.

        try
        {
            string libraryPath = ResolveLibraryPath();
            // RegisterCustomOpLibrary requires an active inference session to
            // take effect. The custom-op domain lives in the ORT environment,
            // so it stays registered for later ORT GenAI sessions.
            using var options = new SessionOptions();
            options.RegisterCustomOpLibrary(libraryPath);
            using var session = new InferenceSession(modelPath, options);
            Console.WriteLine($"Custom ops: registered Swish-24 CPU kernel from {libraryPath}");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _registrationState, -1);
            Console.WriteLine($"Custom ops: failed to register Swish CPU kernel: {ex.Message}");
        }
    }

    private static string ResolveLibraryPath()
    {
        // Prefer the copy deployed next to the managed binaries; fall back to
        // the CMake build output (useful when running from the repo without a
        // full dotnet build).
        string deployed = Path.Combine(AppContext.BaseDirectory, LibraryFileName);
        if (File.Exists(deployed))
            return deployed;

        string repoBuild = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Native", "build", LibraryFileName));
        if (File.Exists(repoBuild))
            return repoBuild;

        throw new FileNotFoundException(
            $"Custom-op library '{LibraryFileName}' not found. " +
            "Build it via NemotronSpeech/Native/build.ps1 (runs automatically on dotnet build).",
            deployed);
    }

    /// <summary>
    /// True when the model opts in via genai_config.json
    /// (model.session.session_options.use_swish_custom_op) or when any
    /// ai.onnx-domain Swish node is referenced by the config. This keeps the
    /// check cheap — no full ONNX parsing on the managed side.
    /// </summary>
    private static bool ModelNeedsSwishKernel(string modelPath)
    {
        try
        {
            string configPath = Path.Combine(modelPath, "genai_config.json");
            if (!File.Exists(configPath))
                return false;

            using var json = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = json.RootElement;

            // Explicit opt-in flag in the model config.
            if (root.TryGetProperty("model", out var model) &&
                model.TryGetProperty("session", out var session) &&
                session.TryGetProperty("session_options", out var sessionOptions) &&
                sessionOptions.TryGetProperty("use_swish_custom_op", out var flag) &&
                (flag.ValueKind == JsonValueKind.True ||
                 (flag.ValueKind == JsonValueKind.String && flag.GetString() == "1")))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Custom ops: could not inspect genai_config.json: {ex.Message}");
            return false;
        }
    }
}
