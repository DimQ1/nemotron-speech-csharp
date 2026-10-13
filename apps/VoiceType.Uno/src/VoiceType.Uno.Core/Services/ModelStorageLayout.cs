namespace VoiceType.Uno.Services;

/// <summary>The three kinds of on-device model, each with its own sub-folder of the models root.</summary>
public enum ModelKind
{
    /// <summary>Speech recognition (Nemotron, Parakeet, Qwen3...): <c>asr/&lt;model-folder&gt;/</c>.</summary>
    Speech,

    /// <summary>Language models (Gemma, <c>.litertlm</c> files and their engine cache): <c>llm/</c>.</summary>
    Language,

    /// <summary>Speaker detection (Nemotron 3 Diarization): <c>diarization/&lt;model-folder&gt;/</c>.</summary>
    Speaker,
}

/// <summary>
/// The single source of truth for where models live: ONE root (the "Model storage folder" setting, or
/// <see cref="AppPaths.ModelsDir"/>) with one sub-folder per kind. No other code builds model paths.
/// </summary>
/// <remarks>
/// <code>
/// &lt;root&gt;/asr/&lt;model-folder&gt;/...          speech recognition
/// &lt;root&gt;/llm/&lt;file&gt;.litertlm (+ cache)    language models
/// &lt;root&gt;/diarization/&lt;model-folder&gt;/...  speaker detection
/// </code>
/// Older versions kept everything flat in the root; <see cref="ModelStorageMigrator"/> moves such content into the
/// sub-folders, and the scan helpers (<see cref="AsrScanRoots"/>, <see cref="DiarizationScanRoots"/>) still accept
/// the flat root until that has happened.
/// </remarks>
public sealed record ModelStorageLayout
{
    public const string AsrFolderName = "asr";
    public const string LlmFolderName = "llm";
    public const string DiarizationFolderName = "diarization";

    private static Func<string?> s_rootSetting = static () => null;

    public ModelStorageLayout(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The models root is empty.", nameof(root));
        Root = Path.GetFullPath(root.Trim());
    }

    /// <summary>The models root: the parent of the three kind folders.</summary>
    public string Root { get; }

    public string AsrDir => Path.Combine(Root, AsrFolderName);
    public string LlmDir => Path.Combine(Root, LlmFolderName);
    public string DiarizationDir => Path.Combine(Root, DiarizationFolderName);

    /// <summary>The folder of a kind.</summary>
    public string DirOf(ModelKind kind) => Path.Combine(Root, FolderOf(kind));

    /// <summary>Short folder name of a kind (<c>asr</c>, <c>llm</c>, <c>diarization</c>).</summary>
    public static string FolderOf(ModelKind kind) => kind switch
    {
        ModelKind.Speech => AsrFolderName,
        ModelKind.Language => LlmFolderName,
        ModelKind.Speaker => DiarizationFolderName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Full path of a speech model folder, e.g. <c>&lt;root&gt;/asr/parakeet-tdt-0.6b-v3-onnx-int4</c>.</summary>
    public string AsrModelPath(string folder) => Path.Combine(AsrDir, folder);

    /// <summary>Full path of a language model file, e.g. <c>&lt;root&gt;/llm/gemma-4-E2B-it.litertlm</c>.</summary>
    public string LlmFilePath(string fileName) => Path.Combine(LlmDir, fileName);

    /// <summary>Full path of a speaker model folder, e.g. <c>&lt;root&gt;/diarization/nemotron-3-diarization-onnx-int8</c>.</summary>
    public string DiarizationModelPath(string folder) => Path.Combine(DiarizationDir, folder);

    /// <summary>Where speech models are looked for: <c>asr/</c> first, then the flat root of older versions.</summary>
    public IReadOnlyList<string> AsrScanRoots => [AsrDir, Root];

    /// <summary>Where speaker models are looked for: <c>diarization/</c> first, then the flat root of older versions.</summary>
    public IReadOnlyList<string> DiarizationScanRoots => [DiarizationDir, Root];

    /// <summary>Creates the three kind folders (idempotent).</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(AsrDir);
        Directory.CreateDirectory(LlmDir);
        Directory.CreateDirectory(DiarizationDir);
    }

    /// <summary>True when <paramref name="path"/> is inside this layout's root.</summary>
    public bool Contains(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            var prefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>The layout of a "Model storage folder" setting; blank means <paramref name="defaultRoot"/>.</summary>
    public static ModelStorageLayout Resolve(string? rootSetting, string defaultRoot) =>
        new(string.IsNullOrWhiteSpace(rootSetting) ? defaultRoot : rootSetting);

    /// <summary>The layout of the saved settings: <see cref="AppSettings.ModelsRootPath"/>, or <see cref="AppPaths.ModelsDir"/>.</summary>
    public static ModelStorageLayout Resolve(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Resolve(settings.ModelsRootPath, AppPaths.ModelsDir);
    }

    /// <summary>
    /// The layout in effect now. The app registers a provider that reads the live settings
    /// (<see cref="UseRootProvider"/>); without one (tests, tools) it is the default root.
    /// </summary>
    public static ModelStorageLayout Current => Resolve(s_rootSetting(), AppPaths.ModelsDir);

    /// <summary>Registers where <see cref="Current"/> reads the "Model storage folder" setting from.</summary>
    public static void UseRootProvider(Func<string?> rootSetting)
    {
        ArgumentNullException.ThrowIfNull(rootSetting);
        s_rootSetting = rootSetting;
    }

    /// <summary>How paths compare on this OS (case-insensitive on Windows and macOS).</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
