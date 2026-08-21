namespace VoiceType.Uno.Services;

/// <summary>Finds and normalizes installed model directories for all UNO targets.</summary>
public static class ModelPathResolver
{
    public static string? FindExistingModelPath(AppSettings settings)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredRoot = NormalizePath(settings.ModelsRootPath);

        if (configuredRoot is not null)
        {
            if (!string.IsNullOrWhiteSpace(settings.SelectedModel))
                AddCandidate(candidates, seen, Path.Combine(configuredRoot, settings.SelectedModel));

            AddCandidate(candidates, seen, configuredRoot);
            AddModelDirectories(candidates, seen, configuredRoot);
        }

        AddCandidate(candidates, seen, settings.ModelPath);

        var defaultRoot = NormalizePath(AppPaths.ModelsDir);
        if (defaultRoot is not null)
        {
            AddCandidate(candidates, seen, defaultRoot);
            AddModelDirectories(candidates, seen, defaultRoot);
        }

        AddDevelopmentCandidates(candidates, seen);
        return candidates.FirstOrDefault(IsModelDirectory);
    }

    public static bool ApplyExistingModelPath(AppSettings settings)
    {
        var modelPath = FindExistingModelPath(settings);
        if (modelPath is null)
            return false;

        var changed = false;
        if (!PathsEqual(settings.ModelPath, modelPath))
        {
            settings.ModelPath = modelPath;
            changed = true;
        }

        var modelRoot = Path.GetDirectoryName(modelPath);
        if (modelRoot is not null && !PathsEqual(settings.ModelsRootPath, modelRoot))
        {
            settings.ModelsRootPath = modelRoot;
            changed = true;
        }

        var selectedModel = Path.GetFileName(modelPath);
        if (!string.Equals(settings.SelectedModel, selectedModel, StringComparison.OrdinalIgnoreCase))
        {
            settings.SelectedModel = selectedModel;
            changed = true;
        }

        return changed;
    }

    private static void AddModelDirectories(List<string> candidates, HashSet<string> seen, string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            foreach (var directory in Directory.GetDirectories(root))
                AddCandidate(candidates, seen, directory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddDevelopmentCandidates(List<string> candidates, HashSet<string> seen)
    {
        var basePath = AppContext.BaseDirectory;
        AddCandidate(candidates, seen, Path.Combine(
            basePath, "..", "..", "..", "..", "..", "..",
            "models", "asr", "nemotron-3.5", "onnx", "cpu-int4"));
        AddCandidate(candidates, seen, Path.Combine(
            basePath, "..", "..", "..", "..", "..", "..",
            "modules", "asr", "cpu"));
    }

    private static void AddCandidate(List<string> candidates, HashSet<string> seen, string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is not null && seen.Add(normalized))
            candidates.Add(normalized);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            path = path.Trim();
            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsModelDirectory(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "genai_config.json"));

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
