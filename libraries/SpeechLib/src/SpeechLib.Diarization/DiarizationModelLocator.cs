namespace SpeechLib.Diarization;

/// <summary>Weight precision of the step graph.</summary>
public enum DiarizationPrecision
{
    /// <summary>Pick int8 when the folder has it, otherwise fp32.</summary>
    Auto,
    /// <summary><c>diar_step.onnx</c>, 398 MB, reference accuracy.</summary>
    Fp32,
    /// <summary><c>diar_step.int8.onnx</c>, 105 MB, 2-3x faster, same DER within +-0.5.</summary>
    Int8,
}

/// <summary>One file of the model distribution.</summary>
/// <param name="Name">File name inside the model folder.</param>
/// <param name="ExpectedBytes">Size of the published export. Used for the catalog and, loosely, for validation.</param>
/// <param name="MinBytes">Smallest plausible size; anything below is treated as a truncated download.</param>
public sealed record DiarizationModelFile(string Name, long ExpectedBytes, long MinBytes);

/// <summary>A validated model folder.</summary>
public sealed record DiarizationModelDescriptor(
    string Folder,
    DiarizationPrecision Precision,
    string StepFile,
    string FrontendFile,
    string ConfigFile,
    long TotalBytes,
    IReadOnlyList<DiarizationPrecision> AvailablePrecisions)
{
    /// <summary>Full path of the step graph.</summary>
    public string StepPath => Path.Combine(Folder, StepFile);
    /// <summary>Full path of the mel front-end graph.</summary>
    public string FrontendPath => Path.Combine(Folder, FrontendFile);
    /// <summary>Full path of <c>diar_config.json</c>.</summary>
    public string ConfigPath => Path.Combine(Folder, ConfigFile);
}

/// <summary>
/// Where the model lives and what it consists of. The app downloads the files into a folder named
/// <see cref="Int8FolderName"/> (int8 only), <see cref="Fp32FolderName"/> (fp32 only) or <see cref="FolderName"/> (everything)
/// under its models root, the same <c>{repo}-{variant}</c> naming <c>ModelDescriptor.SubfolderName</c> produces;
/// <see cref="Find"/> and <see cref="Validate"/> turn that into a loadable descriptor.
/// </summary>
/// <remarks>
/// For the model catalog (not wired yet): one catalog entry per variant, each a list of
/// <see cref="Int8Files"/> / <see cref="Fp32Files"/> (+ <see cref="CommonFiles"/>) at the repo root, a
/// model-kind "diarization" that is not an ASR provider, <see cref="UpstreamRepo"/> and <see cref="License"/>
/// to show in the UI, and the sizes below (rendered in Models, Speakers). See
/// <c>tools/converters/NemotronDiarization/publish_hf.md</c> for the upload.
/// </remarks>
public static class DiarizationModelLocator
{
    /// <summary>Folder with all files (fp32 + int8 + front-end + config, about 503 MB): the spike's export folder.</summary>
    public const string FolderName = "nemotron-3-diarization-onnx";
    /// <summary>Folder with the int8 step graph only (about 106 MB). The default download, and the only one on phones.</summary>
    public const string Int8FolderName = "nemotron-3-diarization-onnx-int8";
    /// <summary>Folder with the fp32 step graph only (about 399 MB).</summary>
    public const string Fp32FolderName = "nemotron-3-diarization-onnx-fp32";

    /// <summary>Upstream PyTorch model.</summary>
    public const string UpstreamRepo = "nvidia/Nemotron-3-Diarization";
    /// <summary>License the UI has to show.</summary>
    public const string License = "OpenMDW-1.1";

    public const string FrontendFileName = "diar_frontend.onnx";
    public const string ConfigFileName = "diar_config.json";
    public const string Fp32StepFileName = "diar_step.onnx";
    public const string Int8StepFileName = "diar_step.int8.onnx";

    /// <summary>Files every variant needs.</summary>
    public static IReadOnlyList<DiarizationModelFile> CommonFiles { get; } =
    [
        new(FrontendFileName, 1_186_102, 1_000_000),
        new(ConfigFileName, 13_184, 3_000),
    ];

    /// <summary>The int8 step graph.</summary>
    public static IReadOnlyList<DiarizationModelFile> Int8Files { get; } =
        [new(Int8StepFileName, 104_653_656, 90_000_000)];

    /// <summary>The fp32 step graph.</summary>
    public static IReadOnlyList<DiarizationModelFile> Fp32Files { get; } =
        [new(Fp32StepFileName, 397_841_111, 350_000_000)];

    /// <summary>Download size of the int8 variant (front-end, config and int8 step graph).</summary>
    public static long Int8DownloadBytes => Sum(CommonFiles) + Sum(Int8Files);

    /// <summary>Download size of the fp32 variant.</summary>
    public static long Fp32DownloadBytes => Sum(CommonFiles) + Sum(Fp32Files);

    /// <summary>
    /// Looks for <see cref="Int8FolderName"/>, <see cref="Fp32FolderName"/> and <see cref="FolderName"/> directly under
    /// each root (in that order, so a lean int8 install wins for <see cref="DiarizationPrecision.Auto"/>; when
    /// <paramref name="precision"/> is explicit, folders without that graph are skipped) and returns the first folder
    /// that validates, or null.
    /// </summary>
    public static DiarizationModelDescriptor? Find(IEnumerable<string> modelRoots,
        DiarizationPrecision precision = DiarizationPrecision.Auto)
    {
        foreach (var root in modelRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var name in new[] { Int8FolderName, Fp32FolderName, FolderName })
            {
                string dir = Path.Combine(root, name);
                if (Directory.Exists(dir) && TryValidate(dir, precision, out var d, out _))
                    return d;
            }
        }
        return null;
    }

    /// <summary>Validates a folder and returns its descriptor, throwing <see cref="DiarizationModelException"/> with a clear message.</summary>
    public static DiarizationModelDescriptor Validate(string folder, DiarizationPrecision precision = DiarizationPrecision.Auto)
    {
        if (!TryValidate(folder, precision, out var d, out var error))
            throw new DiarizationModelException(error!);
        return d!;
    }

    /// <summary>Non-throwing <see cref="Validate"/>.</summary>
    public static bool TryValidate(string folder, DiarizationPrecision precision,
        out DiarizationModelDescriptor? descriptor, out string? error)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            error = $"Diarization model folder '{folder}' does not exist.";
            return false;
        }

        foreach (var f in CommonFiles)
            if (!CheckFile(folder, f, out error)) return false;

        bool hasInt8 = IsComplete(folder, Int8Files[0], out var int8Error);
        bool hasFp32 = IsComplete(folder, Fp32Files[0], out var fp32Error);
        var available = new List<DiarizationPrecision>();
        if (hasFp32) available.Add(DiarizationPrecision.Fp32);
        if (hasInt8) available.Add(DiarizationPrecision.Int8);

        DiarizationPrecision chosen;
        switch (precision)
        {
            case DiarizationPrecision.Int8 when !hasInt8:
                error = int8Error;
                return false;
            case DiarizationPrecision.Fp32 when !hasFp32:
                error = fp32Error;
                return false;
            case DiarizationPrecision.Auto when !hasInt8 && !hasFp32:
                error = $"'{folder}' has neither {Int8StepFileName} nor {Fp32StepFileName}. {int8Error}";
                return false;
            case DiarizationPrecision.Auto:
                chosen = hasInt8 ? DiarizationPrecision.Int8 : DiarizationPrecision.Fp32;
                break;
            default:
                chosen = precision;
                break;
        }

        string step = chosen == DiarizationPrecision.Int8 ? Int8StepFileName : Fp32StepFileName;
        long total = Sum(CommonFiles) + new FileInfo(Path.Combine(folder, step)).Length;
        descriptor = new DiarizationModelDescriptor(Path.GetFullPath(folder), chosen, step, FrontendFileName,
            ConfigFileName, total, available);
        error = null;
        return true;
    }

    private static bool IsComplete(string folder, DiarizationModelFile file, out string? error) =>
        CheckFile(folder, file, out error);

    private static bool CheckFile(string folder, DiarizationModelFile file, out string? error)
    {
        string path = Path.Combine(folder, file.Name);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            error = $"Diarization model file '{file.Name}' is missing from '{folder}'.";
            return false;
        }
        if (info.Length < file.MinBytes)
        {
            error = $"Diarization model file '{file.Name}' in '{folder}' is {info.Length:N0} bytes, expected about " +
                    $"{file.ExpectedBytes:N0}; the download looks incomplete.";
            return false;
        }
        error = null;
        return true;
    }

    private static long Sum(IReadOnlyList<DiarizationModelFile> files)
    {
        long s = 0;
        foreach (var f in files) s += f.ExpectedBytes;
        return s;
    }
}
