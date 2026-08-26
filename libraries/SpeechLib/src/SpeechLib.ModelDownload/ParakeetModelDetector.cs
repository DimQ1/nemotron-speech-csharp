using System.Text.Json;

namespace SpeechLib.ModelDownload;

/// <summary>
/// Detects Parakeet TDT ONNX exports without taking a dependency on the
/// inference assembly. A Parakeet export is a folder containing
/// <c>config.json</c> with <c>model_type: "nemo-conformer-tdt"</c>.
/// </summary>
public static class ParakeetModelDetector
{
    public const string ParakeetModelType = "nemo-conformer-tdt";

    public static bool IsParakeetTdtModel(string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir)) return false;
        var config = Path.Combine(modelDir, "config.json");
        if (!File.Exists(config)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            return doc.RootElement.TryGetProperty("model_type", out var t)
                && t.GetString() == ParakeetModelType;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
