using SpeechLib.ModelDownload;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

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
        }

        // A concrete ModelPath is more authoritative than directory scanning, but
        // only after the current root/selection pair has had a chance to resolve.
        AddCandidate(candidates, seen, settings.ModelPath);

        if (configuredRoot is not null)
        {

            AddCandidate(candidates, seen, configuredRoot);
            AddModelDirectories(candidates, seen, configuredRoot);
        }

        var defaultRoot = NormalizePath(AppPaths.ModelsDir);
        if (ShouldSearchDefaultRoot(configuredRoot, defaultRoot))
        {
            AddCandidate(candidates, seen, defaultRoot);
            AddModelDirectories(candidates, seen, defaultRoot);
        }

        AddDevelopmentModelCandidate(candidates, seen, settings.ExecutionProvider);

        return candidates.FirstOrDefault(IsModelDirectory);
    }

    public static bool ApplyExistingModelPath(AppSettings settings)
    {
        var modelPath = FindExistingModelPath(settings);
        if (modelPath is null)
            return false;

        var changed = false;
        if (!string.Equals(settings.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            settings.ModelPath = modelPath;
            changed = true;
        }

        var modelRoot = Path.GetDirectoryName(modelPath);
        if (modelRoot is not null
            && !string.Equals(settings.ModelsRootPath, modelRoot, StringComparison.OrdinalIgnoreCase))
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

            foreach (var directory in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                AddCandidate(candidates, seen, directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool ShouldSearchDefaultRoot(string? configuredRoot, string? defaultRoot)
    {
        if (defaultRoot is null || configuredRoot is null)
            return true;

        if (string.Equals(configuredRoot, defaultRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        // Recover from a removed custom root or the pre-package default path, but
        // do not replace a deliberate, existing custom root just because it is empty.
        if (!Directory.Exists(configuredRoot))
            return true;

        var legacyRoot = NormalizePath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceType",
            "Models"));

        return legacyRoot is not null
            && !string.Equals(legacyRoot, defaultRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(configuredRoot, legacyRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDevelopmentModelCandidate(
        List<string> candidates,
        HashSet<string> seen,
        string executionProvider)
    {
        if (AppPaths.IsPackaged)
            return;

        var subfolder = executionProvider.ToLowerInvariant() switch
        {
            "cuda" => "gpu-cuda",
            "dml" => "gpu-cuda",
            _ => "cpu"
        };

        AddCandidate(candidates, seen, Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "modules",
            "asr",
            subfolder));
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
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
    }

    private static bool IsModelDirectory(string path)
        => ModelFolderScanner.IsModelDirectory(path);
}