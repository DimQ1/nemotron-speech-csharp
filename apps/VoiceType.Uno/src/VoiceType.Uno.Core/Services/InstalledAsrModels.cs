using SpeechLib.ModelDownload;
using SpeechLib.ModelFormats;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Services;

/// <summary>Recognizer family of a model folder, as written to <c>versions[].model.family</c>.</summary>
public static class AsrModelFamilies
{
    public const string ParakeetTdt = "ParakeetTdt";
    public const string Nemotron = "Nemotron";
    public const string VibeVoice = "VibeVoice";
    public const string Qwen3Asr = "Qwen3Asr";
    public const string Qwen3AsrStreaming = "Qwen3AsrStreaming";

    /// <summary>Same detection order as <c>RecognizerFactory.Create</c>.</summary>
    public static string Detect(string modelPath)
    {
        if (VibeVoiceModelDetector.IsVibeVoiceAsrModel(modelPath))
            return VibeVoice;
        if (Qwen3ModelDetector.IsQwen3AsrStreamingModel(modelPath))
            return Qwen3AsrStreaming;
        if (Qwen3ModelDetector.IsQwen3AsrModel(modelPath))
            return Qwen3Asr;
        if (ParakeetModelDetector.IsParakeetTdtModel(modelPath))
            return ParakeetTdt;
        return Nemotron;
    }
}

/// <summary>An ASR model folder found on disk, with its catalog entry when it has one.</summary>
public sealed record InstalledAsrModel(string Folder, string Path, ModelDescriptor? Descriptor, string Family)
{
    /// <summary>Catalog title, or the folder name for a custom model.</summary>
    public string Title => Descriptor?.Title ?? KnownModels.Describe(Folder);

    /// <summary>Catalog WER (CV17 ru+en), percent; null when not measured.</summary>
    public double? WerPercent => Descriptor?.Research.Wer?.TotalPercent;

    /// <summary>Catalog real-time factor (compute seconds per audio second); null when not measured.</summary>
    public double? RealTimeFactor => Descriptor?.Research.Speed?.RealTimeFactor;

    /// <summary>Metadata written to a transcript version (design §5.2 <c>model</c>).</summary>
    public ModelInfo ToModelInfo() => new()
    {
        CatalogRepoId = Descriptor?.RepoId,
        Folder = Folder,
        Title = Title,
        Precision = Descriptor?.Precision.ToString(),
        Variant = Descriptor?.Variant,
        Family = Family,
    };
}

/// <summary>Finds installed ASR models (complete folders only) under the model roots.</summary>
public static class InstalledAsrModels
{
    /// <summary>
    /// Where speech models are looked for: <c>asr/</c> of the model storage folder (then its flat root, which older
    /// versions used and which may hold folders the user added), the folder of the selected model, then the same for the
    /// app's default models folder. Folders found earlier win.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roots = new List<string?>();
        roots.AddRange(ModelStorageLayout.Resolve(settings).AsrScanRoots);
        if (!string.IsNullOrWhiteSpace(settings.ModelPath))
            roots.Add(System.IO.Path.GetDirectoryName(settings.ModelPath.TrimEnd('/', '\\')));
        roots.AddRange(new ModelStorageLayout(AppPaths.ModelsDir).AsrScanRoots);
        return Distinct(roots);
    }

    /// <summary>Every complete ASR model folder directly under <paramref name="roots"/>, catalog order first.</summary>
    public static IReadOnlyList<InstalledAsrModel> Scan(IEnumerable<string?> roots)
    {
        var result = new List<InstalledAsrModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Distinct(roots))
        {
            IEnumerable<string> directories;
            try
            {
                if (!Directory.Exists(root))
                    continue;
                directories = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var folder = System.IO.Path.GetFileName(directory);
                if (!seen.Contains(folder) && TryDescribe(directory) is { } model)
                {
                    seen.Add(folder);
                    result.Add(model);
                }
            }
        }

        var catalogOrder = ModelCatalog.OrderedModels.Select(m => m.SubfolderName).ToList();
        return result
            .OrderBy(m => m.Descriptor is null ? int.MaxValue : catalogOrder.FindIndex(f => string.Equals(f, m.Folder, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(m => m.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The installed folder named <paramref name="folder"/> under the first root that has it.</summary>
    public static InstalledAsrModel? Find(string folder, IEnumerable<string?> roots)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        foreach (var root in Distinct(roots))
        {
            var path = System.IO.Path.Combine(root, folder);
            if (Directory.Exists(path) && TryDescribe(path) is { } model)
                return model;
        }
        return null;
    }

    /// <summary>Describes a complete model folder, or null when it is not a usable ASR model.</summary>
    public static InstalledAsrModel? TryDescribe(string path)
    {
        if (ModelPathResolver.CheckIntegrity(path) != ModelPathResolver.ModelIntegrity.Complete)
            return null;
        var folder = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
        return new InstalledAsrModel(folder, path, ModelCatalog.FindBySubfolder(folder), AsrModelFamilies.Detect(path));
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string?> roots)
    {
        var result = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            string full;
            try { full = System.IO.Path.GetFullPath(root); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { continue; }
            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase))
                result.Add(full);
        }
        return result;
    }
}
