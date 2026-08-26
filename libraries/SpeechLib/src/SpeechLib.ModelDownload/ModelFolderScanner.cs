namespace SpeechLib.ModelDownload;

/// <summary>
/// Scans a models root folder for usable ASR model directories.
/// Recognizes Nemotron GenAI exports (<c>genai_config.json</c>) and
/// Parakeet TDT exports (<c>config.json</c> with <c>model_type:
/// "nemo-conformer-tdt"</c>). Shared by the WinUI and Uno settings screens
/// and the model path resolver so all of them detect the same folders.
/// </summary>
public static class ModelFolderScanner
{
    /// <summary>True when <paramref name="modelDir"/> is a recognized model folder.</summary>
    public static bool IsModelDirectory(string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            return false;

        return File.Exists(Path.Combine(modelDir, "genai_config.json"))
            || ParakeetModelDetector.IsParakeetTdtModel(modelDir);
    }

    /// <summary>
    /// Returns the names of model folders directly under <paramref name="modelsRoot"/>,
    /// sorted ordinal-ignore-case. Empty when the root is missing/unreadable.
    /// </summary>
    public static IReadOnlyList<string> ScanModelFolderNames(string? modelsRoot)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Directory.Exists(modelsRoot))
            return names;

        try
        {
            foreach (var dir in Directory.GetDirectories(modelsRoot))
            {
                if (IsModelDirectory(dir))
                    names.Add(Path.GetFileName(dir));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
