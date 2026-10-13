using SpeechLib.Diarization;

namespace VoiceType.Uno.Services;

/// <summary>One line of the Model storage block: a kind of model, its folder, how many are installed and how big they are.</summary>
public sealed record ModelStorageKindLine(ModelKind Kind, string Folder, string Path, int Count, long Bytes)
{
    /// <summary><c>Speech recognition · asr/ · 2 models · 1.4 GB</c>.</summary>
    public string Text => ModelStorageText.KindLine(this);
}

/// <summary>What is stored under the models root, for the Settings and Models pages.</summary>
public sealed record ModelStorageSummary(string Root, IReadOnlyList<ModelStorageKindLine> Lines, long? FreeBytes, string Note)
{
    public long TotalBytes => Lines.Sum(l => l.Bytes);
    public int TotalCount => Lines.Sum(l => l.Count);

    /// <summary><c>Total 5.1 GB · 214 GB free</c>.</summary>
    public string TotalText => ModelStorageText.Total(TotalBytes, FreeBytes);

    public ModelStorageKindLine Line(ModelKind kind) => Lines.First(l => l.Kind == kind);
}

/// <summary>Measures what is stored under a <see cref="ModelStorageLayout"/>.</summary>
public static class ModelStorageScanner
{
    /// <summary>Counts and sizes of every kind, plus the free space of the drive (legacy flat content counts until migrated).</summary>
    public static ModelStorageSummary Summarize(ModelStorageLayout layout, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var lines = new List<ModelStorageKindLine>
        {
            Speech(layout),
            Language(layout),
            Speaker(layout),
        };
        return new ModelStorageSummary(layout.Root, lines, FreeBytes(layout.Root), note ?? "");
    }

    private static ModelStorageKindLine Speech(ModelStorageLayout layout)
    {
        var models = InstalledAsrModels.Scan(layout.AsrScanRoots);
        return new ModelStorageKindLine(ModelKind.Speech, ModelStorageLayout.AsrFolderName, layout.AsrDir,
            models.Count, models.Sum(m => ModelStorage.SizeOf(m.Path)));
    }

    private static ModelStorageKindLine Language(ModelStorageLayout layout)
    {
        var count = 0;
        long bytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { layout.LlmDir, layout.Root })
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.litertlm*"))
                {
                    var name = System.IO.Path.GetFileName(file);
                    if (name.EndsWith(".litertlm", StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                        count++;
                    bytes += new FileInfo(file).Length; // the model, a partial download and the engine cache next to it
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read counts as empty.
            }
        }
        return new ModelStorageKindLine(ModelKind.Language, ModelStorageLayout.LlmFolderName, layout.LlmDir, count, bytes);
    }

    private static ModelStorageKindLine Speaker(ModelStorageLayout layout)
    {
        var count = 0;
        long bytes = 0;
        foreach (var model in DiarizationModels.Installed(layout))
        {
            count++;
            bytes += ModelStorage.SizeOf(model.Folder);
        }
        return new ModelStorageKindLine(ModelKind.Speaker, ModelStorageLayout.DiarizationFolderName, layout.DiarizationDir, count, bytes);
    }

    /// <summary>Free bytes of the drive holding <paramref name="path"/> (or its nearest existing parent); null when unknown.</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            var probe = System.IO.Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = System.IO.Path.GetDirectoryName(probe) ?? "";
            if (string.IsNullOrEmpty(probe))
                return null;
            return new DriveInfo(System.IO.Path.GetPathRoot(probe) ?? probe).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Installed speaker-detection model folders (the locator's folder names, valid files only).</summary>
public static class DiarizationModels
{
    /// <summary>Where the locator looks: <c>diarization/</c> of the model storage folder, its flat root, then the same for the default folder.</summary>
    public static IReadOnlyList<string> Roots(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roots = new List<string>();
        foreach (var root in ModelStorageLayout.Resolve(settings).DiarizationScanRoots.Concat(new ModelStorageLayout(AppPaths.ModelsDir).DiarizationScanRoots))
        {
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                roots.Add(root);
        }
        return roots;
    }

    /// <summary>Valid folders under <c>diarization/</c> (then the flat root of older versions), in locator order.</summary>
    public static IReadOnlyList<DiarizationModelDescriptor> Installed(ModelStorageLayout layout)
    {
        var result = new List<DiarizationModelDescriptor>();
        foreach (var root in layout.DiarizationScanRoots)
        {
            foreach (var name in new[]
                     {
                         DiarizationModelLocator.Int8FolderName,
                         DiarizationModelLocator.Fp32FolderName,
                         DiarizationModelLocator.FolderName,
                     })
            {
                var dir = System.IO.Path.Combine(root, name);
                if (Directory.Exists(dir)
                    && DiarizationModelLocator.TryValidate(dir, DiarizationPrecision.Auto, out var descriptor, out _)
                    && !result.Any(d => string.Equals(d.Folder, descriptor!.Folder, StringComparison.OrdinalIgnoreCase)))
                    result.Add(descriptor!);
            }
        }
        return result;
    }
}
