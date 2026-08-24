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

    /// <summary>
    /// A model directory is considered usable only when it is complete: the
    /// config parses AND every model file it references is present and non-empty.
    /// </summary>
    private static bool IsModelDirectory(string path) =>
        CheckIntegrity(path) == ModelIntegrity.Complete;

    /// <summary>Integrity of a model directory (used to decide download vs re-download).</summary>
    public enum ModelIntegrity
    {
        /// <summary>Directory does not exist or is empty — must be downloaded.</summary>
        Missing,
        /// <summary>Directory exists but is broken/incomplete — must be re-downloaded.</summary>
        Broken,
        /// <summary>All referenced model files present and non-empty.</summary>
        Complete
    }

    /// <summary>
    /// Validates a model directory. Parses <c>genai_config.json</c>, collects every
    /// <c>filename</c> it references (encoder/decoder/joiner/vad + their .onnx.data
    /// companions), and verifies each file exists and is non-empty. Also flags stale
    /// <c>.part</c> artifacts from interrupted downloads. This tells the UI whether a
    /// model is complete, missing, or broken-and-needs-re-download.
    /// </summary>
    public static ModelIntegrity CheckIntegrity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return ModelIntegrity.Missing;

        var configPath = Path.Combine(path, "genai_config.json");
        if (!File.Exists(configPath))
            return ModelIntegrity.Broken;

        try
        {
            // Stale partial-download artifact → incomplete.
            if (System.IO.Directory.EnumerateFiles(path, "*.part", SearchOption.AllDirectories).Any())
                return ModelIntegrity.Broken;

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            var required = new List<string>();
            CollectFilenames(document.RootElement, required);
            if (required.Count == 0)
                return ModelIntegrity.Broken;

            foreach (var name in required)
            {
                var file = Path.Combine(path, name);
                if (!File.Exists(file) || new FileInfo(file).Length == 0)
                    return ModelIntegrity.Broken;
            }

            return ModelIntegrity.Complete;
        }
        catch (Exception)
        {
            // Unparseable/corrupt config counts as broken.
            return ModelIntegrity.Broken;
        }
    }

    /// <summary>Recursively collects every <c>filename</c> value from the model config.</summary>
    private static void CollectFilenames(System.Text.Json.JsonElement element, List<string> names)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("filename")
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        names.Add(value);
                }
                else
                {
                    CollectFilenames(property.Value, names);
                }
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectFilenames(item, names);
        }
    }

    /// <summary>
    /// True when an ASR model looks partially downloaded or broken: a candidate
    /// directory exists but is not complete (broken) or stale .part files remain.
    /// Used to show a "re-download" affordance instead of a plain "missing" banner.
    /// </summary>
    public static bool HasPartialModel(AppSettings settings)
    {
        try
        {
            var roots = new List<string>();
            var configuredRoot = NormalizePath(settings.ModelsRootPath);
            if (configuredRoot is not null)
                roots.Add(configuredRoot);
            var defaultRoot = NormalizePath(AppPaths.ModelsDir);
            if (defaultRoot is not null)
                roots.Add(defaultRoot);

            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                if (Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any())
                    return true;

                // Any model-shaped dir that is not complete counts as partial.
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    var hasOnnx = Directory.EnumerateFiles(dir, "*.onnx*").Any();
                    if (hasOnnx && CheckIntegrity(dir) != ModelIntegrity.Complete)
                        return true;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
