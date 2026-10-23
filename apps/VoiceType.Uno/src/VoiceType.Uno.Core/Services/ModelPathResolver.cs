using SpeechLib.ModelFormats;

namespace VoiceType.Uno.Services;

/// <summary>Finds and normalizes installed model directories for all UNO targets.</summary>
public static class ModelPathResolver
{
    /// <summary>
    /// The usable speech model: the selected folder under <c>&lt;root&gt;/asr</c> (or, until migrated, the flat root), the
    /// stored <see cref="AppSettings.ModelPath"/>, then any complete model folder under the model storage folder and the
    /// default one. Folders the user copied into <c>asr/</c> are found like downloaded ones.
    /// </summary>
    public static string? FindExistingModelPath(AppSettings settings)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredRoot = NormalizePath(settings.ModelsRootPath);

        if (configuredRoot is not null && !string.IsNullOrWhiteSpace(settings.SelectedModel))
        {
            var layout = new ModelStorageLayout(configuredRoot);
            AddCandidate(candidates, seen, layout.AsrModelPath(settings.SelectedModel));
            AddCandidate(candidates, seen, Path.Combine(layout.Root, settings.SelectedModel)); // flat layout of older versions
        }

        // A concrete ModelPath is more authoritative than directory scanning, but only
        // after the current root/selection pair has had a chance to resolve (same order
        // as VoiceType.WinUI).
        AddCandidate(candidates, seen, settings.ModelPath);

        if (configuredRoot is not null)
        {
            var layout = new ModelStorageLayout(configuredRoot);
            AddModelDirectories(candidates, seen, layout.AsrDir);
            AddCandidate(candidates, seen, layout.Root);
            AddModelDirectories(candidates, seen, layout.Root);
        }

        var defaultRoot = NormalizePath(AppPaths.ModelsDir);
        if (defaultRoot is not null)
        {
            var layout = new ModelStorageLayout(defaultRoot);
            AddModelDirectories(candidates, seen, layout.AsrDir);
            AddCandidate(candidates, seen, layout.Root);
            AddModelDirectories(candidates, seen, layout.Root);
        }

        AddDevelopmentCandidates(candidates, seen);
        return candidates.FirstOrDefault(IsModelDirectory);
    }

    /// <summary>
    /// The folder of a speech model by name under a models root: <c>&lt;root&gt;/asr/&lt;name&gt;</c>, or the flat
    /// <c>&lt;root&gt;/&lt;name&gt;</c> of older versions while it has not been migrated; the <c>asr/</c> path when neither exists.
    /// </summary>
    public static string LocateModelFolder(string modelsRoot, string folderName)
    {
        var layout = new ModelStorageLayout(modelsRoot);
        var inAsr = layout.AsrModelPath(folderName);
        if (Directory.Exists(inAsr))
            return inAsr;
        var flat = Path.Combine(layout.Root, folderName);
        return Directory.Exists(flat) ? flat : inAsr;
    }

    /// <summary>
    /// Points <see cref="AppSettings.ModelPath"/>, the selection and the storage folder at the usable model.
    /// The storage folder is the model's parent (or its parent's parent when the model sits in <c>asr/</c>).
    /// </summary>
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

        // The storage folder follows the model only when the model lives outside both the configured folder and the app's
        // default one. A model found in the default folder while the user chose another (still empty) folder must not
        // undo that choice: it is a fallback, and new downloads go to the chosen folder.
        var modelRoot = StorageRootOf(modelPath);
        var configured = NormalizePath(settings.ModelsRootPath);
        var insideConfigured = configured is not null && new ModelStorageLayout(configured).Contains(modelPath);
        var insideDefault = new ModelStorageLayout(AppPaths.ModelsDir).Contains(modelPath);
        if (modelRoot is not null && !insideConfigured && !insideDefault && !PathsEqual(settings.ModelsRootPath, modelRoot))
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

    /// <summary>The model storage folder a model folder belongs to: the parent of <c>asr/</c>, or the parent for a flat model.</summary>
    internal static string? StorageRootOf(string modelPath)
    {
        var parent = Path.GetDirectoryName(modelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null)
            return null;
        return string.Equals(Path.GetFileName(parent), ModelStorageLayout.AsrFolderName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(parent) ?? parent
            : parent;
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
    /// A model directory is usable when the shared scanner recognises it (Nemotron,
    /// Parakeet TDT, VibeVoice, Qwen3) and it is not broken. Earlier this
    /// accepted only Nemotron/Qwen3-streaming folders, so a Parakeet or VibeVoice
    /// model picked in Settings was silently replaced by the first Nemotron folder.
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
    /// Validates a model directory. Parses <c>genai_config.json</c> (the config file name
    /// of the ONNX Runtime GenAI export format the models are published in), collects every
    /// <c>filename</c> it references (encoder/decoder/joiner/vad + their .onnx.data
    /// companions), and verifies each file exists and is non-empty. Also flags stale
    /// <c>.part</c> artifacts from interrupted downloads. This tells the UI whether a
    /// model is complete, missing, or broken-and-needs-re-download.
    /// </summary>
    public static ModelIntegrity CheckIntegrity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return ModelIntegrity.Missing;

        try
        {
            // Stale partial-download artifact → incomplete, whatever the model kind.
            if (Directory.EnumerateFiles(path, "*.part", SearchOption.AllDirectories).Any())
                return ModelIntegrity.Broken;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (Qwen3ModelDetector.IsQwen3AsrStreamingModel(path))
            return Qwen3ModelDetector.IsCompleteQwen3AsrStreamingModel(path)
                ? ModelIntegrity.Complete
                : ModelIntegrity.Broken;

        if (ParakeetModelDetector.IsParakeetTdtModel(path))
            return HasNonEmptyFiles(path, "encoder-model.onnx", "decoder_joint-model.onnx", "nemo128.onnx", "vocab.txt")
                ? ModelIntegrity.Complete
                : ModelIntegrity.Broken;

        // VibeVoice and plain Qwen3 exports are validated by their detectors' own
        // file checks; a recognised folder is usable.
        if (VibeVoiceModelDetector.IsVibeVoiceAsrModel(path) || Qwen3ModelDetector.IsQwen3AsrModel(path))
            return ModelIntegrity.Complete;

        var configPath = Path.Combine(path, "genai_config.json");
        if (!File.Exists(configPath))
            return ModelIntegrity.Broken;

        try
        {
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

    private static bool HasNonEmptyFiles(string directory, params string[] names)
    {
        foreach (var name in names)
        {
            var file = Path.Combine(directory, name);
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
                return false;
        }
        return true;
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
            if (defaultRoot is not null && !roots.Contains(defaultRoot, StringComparer.OrdinalIgnoreCase))
                roots.Add(defaultRoot);

            foreach (var root in roots)
            {
                var layout = new ModelStorageLayout(root);

                // Speech models: asr/ plus the flat folders of older versions (llm/ and diarization/ have their own partials).
                var folders = new List<string>();
                if (Directory.Exists(layout.AsrDir))
                    folders.AddRange(Directory.EnumerateDirectories(layout.AsrDir));
                if (Directory.Exists(layout.Root))
                {
                    folders.AddRange(Directory.EnumerateDirectories(layout.Root).Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return !string.Equals(name, ModelStorageLayout.AsrFolderName, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(name, ModelStorageLayout.LlmFolderName, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(name, ModelStorageLayout.DiarizationFolderName, StringComparison.OrdinalIgnoreCase)
                            && !name.StartsWith("nemotron-3-diarization-onnx", StringComparison.OrdinalIgnoreCase);
                    }));
                }

                foreach (var folder in folders)
                {
                    if (Directory.EnumerateFiles(folder, "*.part", SearchOption.AllDirectories).Any())
                        return true;

                    // Any model-shaped dir that is not complete counts as partial.
                    foreach (var dir in new[] { folder }.Concat(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)))
                    {
                        var hasOnnx = Directory.EnumerateFiles(dir, "*.onnx*").Any();
                        if (hasOnnx && CheckIntegrity(dir) != ModelIntegrity.Complete)
                            return true;
                    }
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
