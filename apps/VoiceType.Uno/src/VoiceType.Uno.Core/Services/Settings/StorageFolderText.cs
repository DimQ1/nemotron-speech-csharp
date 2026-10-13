using VoiceType.Uno.History;

namespace VoiceType.Uno.Services;

/// <summary>The three folders Settings › Storage lets the user move.</summary>
public enum StorageFolderKind { Models, Recordings, Data }

/// <summary>How the new folder relates to the current one.</summary>
public enum FolderRelation { SameDrive, OtherDrive, Nested }

/// <summary>One piece of a move question: running text, or a folder path (shown in the monospace path style).</summary>
public sealed record StorageTextPart(string Text, bool IsPath = false);

/// <summary>Wording of the Storage cards and the "Move … to the new folder?" questions (design voicetype-uno-settings §4).</summary>
public static class StorageFolderText
{
    public const string RestartNow = "Restart now";
    public const string DontMove = "Don't move";
    public const string OpenFolder = "Open folder";
    public const string ChangeFolder = "Change folder…";
    public const string ChooseAnother = "Choose another…";
    public const string NestedWarning = "The new folder is inside the current one. Choose a folder outside it.";

    public static string Title(StorageFolderKind kind) => kind switch
    {
        StorageFolderKind.Models => ModelStorageText.Title,
        StorageFolderKind.Recordings => "Recordings",
        _ => "App data",
    };

    public static string What(StorageFolderKind kind) => kind switch
    {
        StorageFolderKind.Models => ModelStorageText.Explanation,
        StorageFolderKind.Recordings => "Audio and transcripts saved to History",
        _ => "Settings, chats, the job queue and logs",
    };

    /// <summary><c>Default</c> or <c>Custom</c>.</summary>
    public static string Badge(string path, string defaultPath) => FolderMover.PathsEqual(path, defaultPath) ? "Default" : "Custom";

    /// <summary><c>1.1 GB · 24 recordings</c>.</summary>
    public static string RecordingsFacts(long bytes, int count) =>
        HistoryUiFormat.Bytes(bytes) + " · " + HistoryUiFormat.Count(count, "recording");

    /// <summary>The App data size line: <c>96 MB</c>.</summary>
    public static string DataFacts(long bytes) => HistoryUiFormat.Bytes(bytes);

    /// <summary>What lives in the data root: (name, meaning) pairs for the contents list.</summary>
    public static IReadOnlyList<(string Name, string Meaning)> DataContents { get; } =
    [
        ("settings.json", "everything on these pages"),
        ("chats" + Path.DirectorySeparatorChar, "conversations with Gemma"),
        ("jobs" + Path.DirectorySeparatorChar + "queue.json", "background jobs"),
        ("*.log", "errors, translation, engine host"),
    ];

    public static string QuestionTitle(StorageFolderKind kind) => kind switch
    {
        StorageFolderKind.Models => ModelStorageText.MoveQuestionTitle,
        StorageFolderKind.Recordings => "Move recordings to the new folder?",
        _ => "Move app data to the new folder?",
    };

    /// <summary>Models: <c>4 models (3.4 GB)</c>; recordings: <c>24 recordings (1.1 GB)</c>; data: <c>App data (96 MB)</c>.</summary>
    public static string Subject(StorageFolderKind kind, int count, long bytes) => kind switch
    {
        StorageFolderKind.Models => $"{ModelStorageText.Count(count)} ({DownloadProgressFormatter.FormatBytes(bytes)})",
        StorageFolderKind.Recordings => $"{HistoryUiFormat.Count(count, "recording")} ({HistoryUiFormat.Bytes(bytes)})",
        _ => $"App data ({HistoryUiFormat.Bytes(bytes)})",
    };

    /// <summary>The body of the question, as text and path parts. <see cref="FolderRelation.Nested"/> has no question, only a warning.</summary>
    public static IReadOnlyList<StorageTextPart> Question(StorageFolderKind kind, string subject, string oldPath, string newPath, FolderRelation relation)
    {
        if (relation == FolderRelation.Nested)
            return [new StorageTextPart(NestedWarning)];

        var verb = kind == StorageFolderKind.Data ? "is" : "are";
        if (relation == FolderRelation.OtherDrive)
        {
            var tail = kind == StorageFolderKind.Models
                ? "Models are not copied between drives automatically: they stay in the old folder, and you can copy them into the matching sub-folders (asr, llm, diarization) of the new folder yourself."
                : "Recordings are not copied between drives automatically: they stay in the old folder.";
            return
            [
                new($"{subject} {verb} in"),
                new(oldPath, true),
                new("a different drive than"),
                new(newPath, true),
                new(tail),
            ];
        }

        var after = kind switch
        {
            StorageFolderKind.Models => "They move when the app restarts (same drive: instant, nothing is copied). Or keep them where they are and start the new folder empty.",
            StorageFolderKind.Recordings => "They move when the app restarts (same drive: instant). Or keep them where they are: they then leave History until you choose the old folder again.",
            _ => "VoiceType moves settings, chats, the queue and logs when it restarts. Models and recordings stay in their own folders.",
        };
        return
        [
            new($"{subject} {verb} in"),
            new(oldPath, true),
            new("New folder:"),
            new(newPath, true),
            new(after),
        ];
    }

    /// <summary>Whether "Move on restart" is offered: same drive, or app data (small, so it may cross drives).</summary>
    public static bool OffersMove(StorageFolderKind kind, FolderRelation relation) =>
        relation == FolderRelation.SameDrive || (kind == StorageFolderKind.Data && relation == FolderRelation.OtherDrive);

    /// <summary>"Keep old location" is offered for models and recordings; app data has no such choice (settings without data are useless).</summary>
    public static bool OffersKeep(StorageFolderKind kind, FolderRelation relation) =>
        kind != StorageFolderKind.Data && relation != FolderRelation.Nested;

    public static FolderRelation Relation(string oldPath, string newPath)
    {
        if (FolderMover.IsInside(newPath, oldPath) || FolderMover.IsInside(oldPath, newPath))
            return FolderRelation.Nested;
        return ModelStorageText.SameDrive(oldPath, newPath) ? FolderRelation.SameDrive : FolderRelation.OtherDrive;
    }

    public static string PendingNote(StorageFolderKind kind, string target) => kind switch
    {
        StorageFolderKind.Models => ModelStorageText.PendingMoveNote(target),
        StorageFolderKind.Recordings => $"Recordings will move to {target} when the app restarts.",
        _ => $"App data will move to {target} when the app restarts.",
    };

    /// <summary>
    /// "Keep old location" for recordings: the history store opens once at start, so the new folder starts working after a restart.
    /// </summary>
    public static string RecordingsSwitchNote(string newFolder) =>
        $"New recordings will be saved to {newFolder} after the app restarts. Recordings in the old folder leave History until you choose it again.";

    /// <summary>The note after a start-up move of recordings that could not finish.</summary>
    public static string RecordingsIncompleteNote(string wantedRoot, string actualRoot, IReadOnlyList<FolderMoveSkip> skipped)
    {
        var why = skipped.Any(s => s.Reason == ModelMoveSkipReason.OtherDrive) ? "it is on another drive and recordings are not copied automatically"
            : skipped.Any(s => s.Reason == ModelMoveSkipReason.InUse) ? "some files were in use"
            : skipped.Any(s => s.Reason == ModelMoveSkipReason.DestinationExists) ? "some folders with the same name already exist there"
            : "the folders overlap";
        return $"Recordings were not moved to {wantedRoot}: {why}. They stay in {actualRoot}.";
    }

    /// <summary>The note after a start-up move of app data that could not finish.</summary>
    public static string DataIncompleteNote(string wantedRoot, string actualRoot, IReadOnlyList<FolderMoveSkip> skipped)
    {
        var why = skipped.Any(s => s.Reason == ModelMoveSkipReason.InUse) ? "some files were in use"
            : skipped.Any(s => s.Reason == ModelMoveSkipReason.DestinationExists) ? "some files with the same name already exist there"
            : "the folders overlap";
        return $"App data was not moved to {wantedRoot}: {why}. It stays in {actualRoot}.";
    }

    /// <summary>The note when the saved app data folder cannot be used and the default one is in use.</summary>
    public static string DataUnavailableNote(string wantedRoot, string defaultRoot) =>
        $"The app data folder {wantedRoot} is not available. VoiceType uses the default folder {defaultRoot} instead.";

    /// <summary>The line under the three cards.</summary>
    public static string Footer(bool android) => android
        ? "Android keeps models, recordings and app data in VoiceType’s private storage. Nothing leaves the phone, and Android removes it all when VoiceType is uninstalled."
        : "Everything stays on this computer. A folder change never deletes or overwrites files: moves happen at the next start, as renames on the same drive.";
}
