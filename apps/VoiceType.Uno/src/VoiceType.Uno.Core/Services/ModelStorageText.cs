using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Services;

/// <summary>Wording of the "Model storage" block (Settings, Models page): pure text, testable without a UI.</summary>
public static class ModelStorageText
{
    public static string Title => Loc.T("Models.Storage.Title");

    public static string RootLabel => Loc.T("Models.Storage.RootLabel");

    public static string Explanation => Loc.T("Models.Storage.Explanation");

    public static string OpenFolder => Loc.T("Models.Storage.OpenFolder");

    public static string Browse => Loc.T("Models.Storage.Browse");

    public static string ChangeRootNote => Loc.T("Models.Storage.ChangeRootNote");

    public static string KindTitle(ModelKind kind) => kind switch
    {
        ModelKind.Speech => Loc.T("Models.Storage.Kind.Speech"),
        ModelKind.Language => Loc.T("Models.Storage.Kind.Language"),
        ModelKind.Speaker => Loc.T("Models.Storage.Kind.Speaker"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary><c>Speech recognition · asr/ · 2 models · 1.4 GB</c>; <c>none yet</c> when empty.</summary>
    public static string KindLine(ModelStorageKindLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Count <= 0
            ? Loc.F("Models.Storage.KindLineEmpty", KindTitle(line.Kind), line.Folder)
            : Loc.F("Models.Storage.KindLine", KindTitle(line.Kind), line.Folder, Count(line.Count), DownloadProgressFormatter.FormatBytes(line.Bytes));
    }

    /// <summary><c>1 model</c> / <c>3 models</c>.</summary>
    public static string Count(int count) => Loc.Plural("Models.Storage.ModelCount", count);

    /// <summary><c>Total 5.1 GB · 214 GB free</c>; the free part is omitted when unknown.</summary>
    public static string Total(long totalBytes, long? freeBytes)
    {
        var total = DownloadProgressFormatter.FormatBytes(Math.Max(0, totalBytes));
        return freeBytes is { } free and >= 0
            ? Loc.F("Models.Storage.TotalFree", total, DownloadProgressFormatter.FormatBytes(free))
            : Loc.F("Models.Storage.Total", total);
    }

    /// <summary>The line under the root path: where models come from and where the kind folders are.</summary>
    public static string PathHint(string root) => Loc.F("Models.Storage.PathHint", root);

    /// <summary>"Copy a model folder here" help for one kind, e.g. in the Speakers tab.</summary>
    public static string PlaceFilesText(ModelKind kind, ModelStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return kind switch
        {
            ModelKind.Speech => Loc.F("Models.Storage.PlaceFiles.Speech", layout.AsrDir),
            ModelKind.Language => Loc.F("Models.Storage.PlaceFiles.Language", layout.LlmDir),
            ModelKind.Speaker => Loc.F("Models.Storage.PlaceFiles.Speaker", layout.DiarizationDir),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>Models page line under the cards: where the models of this kind are stored.</summary>
    public static string StoredIn(ModelKind kind, ModelStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Loc.F("Models.Storage.StoredIn", KindTitle(kind), layout.DirOf(kind));
    }

    /// <summary>Title of the question shown when the user picks another storage folder while models exist.</summary>
    public static string MoveQuestionTitle => Loc.T("Models.Storage.MoveQuestionTitle");

    /// <summary>Body of the move question.</summary>
    public static string MoveQuestion(string oldRoot, string newRoot, ModelStorageSummary current, bool sameDrive) =>
        Loc.F(sameDrive ? "Models.Storage.MoveQuestion.SameDrive" : "Models.Storage.MoveQuestion.OtherDrive",
            Count(current.TotalCount), DownloadProgressFormatter.FormatBytes(current.TotalBytes), oldRoot, newRoot);

    public static string MoveButton => Loc.T("Models.Storage.MoveButton");
    public static string KeepButton => Loc.T("Models.Storage.KeepButton");
    public static string CancelButton => Loc.T("Models.Storage.CancelButton");

    /// <summary>Shown while a "Move on restart" is pending.</summary>
    public static string PendingMoveNote(string newRoot) => Loc.F("Models.Storage.PendingMoveNote", newRoot);

    /// <summary>The note shown after a move that could not finish (different drive, in use, name clash).</summary>
    public static string MoveIncompleteNote(string wantedRoot, string actualRoot, IReadOnlyList<ModelMoveSkip> skipped)
    {
        var drive = skipped.Count(s => s.Reason == ModelMoveSkipReason.OtherDrive);
        var inUse = skipped.Count(s => s.Reason == ModelMoveSkipReason.InUse);
        var clash = skipped.Count(s => s.Reason == ModelMoveSkipReason.DestinationExists);
        var why = drive > 0 ? "it is on another drive and models are not copied automatically"
            : inUse > 0 ? "some files were in use"
            : clash > 0 ? "some folders with the same name already exist there"
            : "the folders overlap";
        return $"Models were not moved to {wantedRoot}: {why}. They stay in {actualRoot}.";
    }

    /// <summary>
    /// The translation model of VoiceType.WinUI is on another drive than the model storage folder (<see cref="WinUiDataImport"/>):
    /// it was put into <paramref name="foundDir"/> and the user brings it over.
    /// </summary>
    public static string PreviousVersionModelNote(string foundDir, string wantedDir) =>
        $"{PreviousVersionModelNotePrefix} {foundDir} (another drive, not copied automatically). Move the .litertlm file into {wantedDir} and press Refresh, or download it again.";

    /// <summary>The start of <see cref="PreviousVersionModelNote"/>, to recognise it in the stored setting.</summary>
    public const string PreviousVersionModelNotePrefix = "The translation model of the previous VoiceType is in";

    /// <summary>Whether two folders are on the same drive (so a move is a rename).</summary>
    public static bool SameDrive(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), ModelStorageLayout.PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>Whether a changed root needs the move question: the folders differ and the old one holds models.</summary>
    public static bool NeedsMoveQuestion(string oldRoot, string newRoot, ModelStorageSummary oldSummary)
    {
        if (string.IsNullOrWhiteSpace(newRoot) || string.IsNullOrWhiteSpace(oldRoot))
            return false;
        try
        {
            var a = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(newRoot.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.Equals(a, b, ModelStorageLayout.PathComparison) && oldSummary.TotalCount > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
