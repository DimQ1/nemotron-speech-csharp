using System.Globalization;

namespace VoiceType.Uno.Services;

/// <summary>Wording of the "Model storage" block (Settings, Models page): pure text, testable without a UI.</summary>
public static class ModelStorageText
{
    public const string Title = "Model storage";

    public const string RootLabel = "Model storage folder";

    public const string Explanation =
        "All models live in one folder, one sub-folder per kind. Copy a model folder into the matching sub-folder and it shows up.";

    public const string OpenFolder = "Open folder";

    public const string Browse = "Browse…";

    public const string ChangeRootNote =
        "Changing the folder: models already downloaded can move there when the app restarts (same drive), or stay where they are.";

    public static string KindTitle(ModelKind kind) => kind switch
    {
        ModelKind.Speech => "Speech recognition",
        ModelKind.Language => "Language models (Gemma)",
        ModelKind.Speaker => "Speaker detection",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary><c>Speech recognition · asr/ · 2 models · 1.4 GB</c>; <c>none yet</c> when empty.</summary>
    public static string KindLine(ModelStorageKindLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tail = line.Count <= 0
            ? "none yet"
            : Count(line.Count) + " · " + DownloadProgressFormatter.FormatBytes(line.Bytes);
        return $"{KindTitle(line.Kind)} · {line.Folder}/ · {tail}";
    }

    /// <summary><c>1 model</c> / <c>3 models</c>.</summary>
    public static string Count(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " model" : " models");

    /// <summary><c>Total 5.1 GB · 214 GB free</c>; the free part is omitted when unknown.</summary>
    public static string Total(long totalBytes, long? freeBytes)
    {
        var total = "Total " + DownloadProgressFormatter.FormatBytes(Math.Max(0, totalBytes));
        return freeBytes is { } free and >= 0 ? total + " · " + DownloadProgressFormatter.FormatBytes(free) + " free" : total;
    }

    /// <summary>The line under the root path: where models come from and where the kind folders are.</summary>
    public static string PathHint(string root) => $"{root} — contains asr/, llm/ and diarization/";

    /// <summary>"Copy a model folder here" help for one kind, e.g. in the Speakers tab.</summary>
    public static string PlaceFilesText(ModelKind kind, ModelStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return kind switch
        {
            ModelKind.Speech => $"Your own speech model? Copy its folder into {layout.AsrDir} and press Refresh.",
            ModelKind.Language => $"Your own language model? Copy the .litertlm file into {layout.LlmDir} and press Refresh.",
            ModelKind.Speaker => $"Offline? Copy the model folder (for example nemotron-3-diarization-onnx-int8) into {layout.DiarizationDir} and press Refresh.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>Models page line under the cards: where the models of this kind are stored.</summary>
    public static string StoredIn(ModelKind kind, ModelStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return $"{KindTitle(kind)} are stored in {layout.DirOf(kind)}";
    }

    /// <summary>Title of the question shown when the user picks another storage folder while models exist.</summary>
    public const string MoveQuestionTitle = "Move models to the new folder?";

    /// <summary>Body of the move question.</summary>
    public static string MoveQuestion(string oldRoot, string newRoot, ModelStorageSummary current, bool sameDrive) =>
        sameDrive
            ? $"{Count(current.TotalCount)} ({DownloadProgressFormatter.FormatBytes(current.TotalBytes)}) are in {oldRoot}. "
              + $"They move to {newRoot} when the app restarts (same drive: instant, nothing is copied). Or keep them where they are."
            : $"{Count(current.TotalCount)} ({DownloadProgressFormatter.FormatBytes(current.TotalBytes)}) are in {oldRoot}, "
              + $"a different drive than {newRoot}. Models are not copied between drives automatically: they stay in {oldRoot}, "
              + "and you can copy them into the matching sub-folders (asr, llm, diarization) of the new folder yourself.";

    public const string MoveButton = "Move on restart";
    public const string KeepButton = "Keep old location";
    public const string CancelButton = "Cancel";

    /// <summary>Shown while a "Move on restart" is pending.</summary>
    public static string PendingMoveNote(string newRoot) => $"Models will move to {newRoot} when the app restarts.";

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
