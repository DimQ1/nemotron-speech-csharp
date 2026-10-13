using SpeechLib.Diarization;

namespace VoiceType.Uno.Services;

/// <summary>
/// One downloadable speaker-detection model: a precision of <c>DimQ1/nemotron-3-diarization-onnx</c>.
/// The repo keeps one sub-folder per precision (<c>int8/</c>, <c>fp32/</c>), like the Parakeet repo; the files land in
/// <c>&lt;models root&gt;/diarization/&lt;Folder&gt;</c>, the folder names <see cref="DiarizationModelLocator"/> already finds.
/// </summary>
/// <param name="Id">Stable id (<c>diarization-int8</c>).</param>
/// <param name="RepoSubfolder">Folder of the precision inside the repo (<c>int8</c>, <c>fp32</c>).</param>
/// <param name="Folder">Local folder name under <c>diarization/</c>.</param>
/// <param name="SizeBytes">Download size (the files of the sub-folder).</param>
public sealed record DiarizationCatalogModel(
    string Id, string Title, string RepoId, string RepoSubfolder, string Folder, DiarizationPrecision Precision,
    long SizeBytes, bool IsRecommended, bool PhoneSupported, string Tagline, string Description)
{
    /// <summary>The model folder under <see cref="ModelStorageLayout.Current"/>.</summary>
    public string LocalPath => ModelStorageLayout.Current.DiarizationModelPath(Folder);

    /// <summary>The folder holds a complete model of this precision (the locator's own validation).</summary>
    public bool IsInstalled => IsInstalledIn(ModelStorageLayout.Current);

    /// <summary>Same check against an explicit layout.</summary>
    public bool IsInstalledIn(ModelStorageLayout layout)
    {
        var dir = layout.DiarizationModelPath(Folder);
        return Directory.Exists(dir) && DiarizationModelLocator.TryValidate(dir, Precision, out _, out _);
    }
}

/// <summary>The speaker-detection models offered for download (a separate catalog: the ASR pickers never list them).</summary>
public static class DiarizationModelCatalog
{
    public const string RepoId = "DimQ1/nemotron-3-diarization-onnx";
    public const string DefaultId = "diarization-int8";

    /// <summary>License the UI shows (the repo carries the OpenMDW-1.1 text and a model card).</summary>
    public const string License = DiarizationModelLocator.License;

    /// <summary>Attribution line of the model card, shown on the Speakers tab.</summary>
    public const string Attribution = "Derived from NVIDIA Nemotron-3-Diarization · OpenMDW-1.1";

    public static IReadOnlyList<DiarizationCatalogModel> All { get; } =
    [
        new DiarizationCatalogModel(
            DefaultId, "Nemotron 3 Diarization · INT8", RepoId, "int8", DiarizationModelLocator.Int8FolderName,
            DiarizationPrecision.Int8, DiarizationModelLocator.Int8DownloadBytes, IsRecommended: true, PhoneSupported: true,
            "Recommended. Small and fast.",
            "8-bit weights: about 105 MB, two to three times faster than full precision with the same accuracy "
            + "(within half a point of speaker error). Runs on phones and laptops."),
        new DiarizationCatalogModel(
            "diarization-fp32", "Nemotron 3 Diarization · FP32", RepoId, "fp32", DiarizationModelLocator.Fp32FolderName,
            DiarizationPrecision.Fp32, DiarizationModelLocator.Fp32DownloadBytes, IsRecommended: false, PhoneSupported: false,
            "Reference precision. Larger and slower.",
            "Full-precision weights: about 399 MB. The reference the INT8 model is measured against; "
            + "pick it only when you want the exact original numbers. Desktop only."),
    ];

    public static DiarizationCatalogModel Recommended => All[0];

    public static DiarizationCatalogModel? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(m => string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The model whose local folder is <paramref name="folder"/> (as stored in <c>Speakers.ModelFolder</c>).</summary>
    public static DiarizationCatalogModel? FindByFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : All.FirstOrDefault(m => string.Equals(m.Folder, folder.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Phones get only the models marked <see cref="DiarizationCatalogModel.PhoneSupported"/>.</summary>
    public static IReadOnlyList<DiarizationCatalogModel> ForDevice(bool isPhone) =>
        isPhone ? All.Where(m => m.PhoneSupported).ToList() : All;

    /// <summary>Download-queue key: one job per model, shared by the Models page, History and the Downloads tab.</summary>
    public static string DownloadKey(DiarizationCatalogModel model) => "diarization-" + model.RepoSubfolder;
}
