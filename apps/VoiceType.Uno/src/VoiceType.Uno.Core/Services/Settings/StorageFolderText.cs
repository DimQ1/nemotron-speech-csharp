using VoiceType.Uno.History;
using VoiceType.Uno.Localization;

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
    public static string RestartNow => Loc.T("Settings.Storage.RestartNow");
    public static string DontMove => Loc.T("Settings.Storage.DontMove");
    public static string OpenFolder => Loc.T("Settings.Storage.OpenFolder");
    public static string ChangeFolder => Loc.T("Settings.Storage.ChangeFolder");
    public static string ChooseAnother => Loc.T("Settings.Storage.ChooseAnother");
    public static string NestedWarning => Loc.T("Settings.Storage.NestedWarning");

    public static string Title(StorageFolderKind kind) => kind switch
    {
        StorageFolderKind.Models => ModelStorageText.Title,
        StorageFolderKind.Recordings => Loc.T("Settings.Storage.Recordings.Title"),
        _ => Loc.T("Settings.Storage.Data.Title"),
    };

    public static string What(StorageFolderKind kind) => kind switch
    {
        StorageFolderKind.Models => ModelStorageText.Explanation,
        StorageFolderKind.Recordings => Loc.T("Settings.Storage.Recordings.What"),
        _ => Loc.T("Settings.Storage.Data.What"),
    };

    /// <summary><c>Default</c> or <c>Custom</c>.</summary>
    public static string Badge(string path, string defaultPath) => FolderMover.PathsEqual(path, defaultPath) ? Loc.T("Settings.Storage.Badge.Default") : Loc.T("Settings.Storage.Badge.Custom");

    /// <summary><c>1.1 GB · 24 recordings</c>.</summary>
    public static string RecordingsFacts(long bytes, int count) =>
        HistoryUiFormat.Bytes(bytes) + " · " + RecordingCount(count);

    /// <summary><c>1 recording</c> / <c>24 recordings</c>.</summary>
    private static string RecordingCount(int count) => Loc.Plural("Settings.Storage.Recordings.Count", count);

    /// <summary>The App data size line: <c>96 MB</c>.</summary>
    public static string DataFacts(long bytes) => HistoryUiFormat.Bytes(bytes);

    /// <summary>What lives in the data root: (name, meaning) pairs for the contents list.</summary>
    public static IReadOnlyList<(string Name, string Meaning)> DataContents =>
    [
        ("settings.json", Loc.T("Settings.Storage.Data.Settings")),
        ("chats" + Path.DirectorySeparatorChar, Loc.T("Settings.Storage.Data.Chats")),
        ("jobs" + Path.DirectorySeparatorChar + "queue.json", Loc.T("Settings.Storage.Data.Jobs")),
        ("*.log", Loc.T("Settings.Storage.Data.Logs")),
    ];

    public static string QuestionTitle(StorageFolderKind kind) => kind switch
    {
        StorageFolderKind.Models => ModelStorageText.MoveQuestionTitle,
        StorageFolderKind.Recordings => Loc.T("Settings.Storage.Recordings.QuestionTitle"),
        _ => Loc.T("Settings.Storage.Data.QuestionTitle"),
    };

    /// <summary>Models: <c>4 models (3.4 GB)</c>; recordings: <c>24 recordings (1.1 GB)</c>; data: <c>App data (96 MB)</c>.</summary>
    public static string Subject(StorageFolderKind kind, int count, long bytes) => kind switch
    {
        StorageFolderKind.Models => Loc.F("Settings.Storage.Subject", ModelStorageText.Count(count), DownloadProgressFormatter.FormatBytes(bytes)),
        StorageFolderKind.Recordings => Loc.F("Settings.Storage.Subject", RecordingCount(count), HistoryUiFormat.Bytes(bytes)),
        _ => Loc.F("Settings.Storage.Data.Subject", HistoryUiFormat.Bytes(bytes)),
    };

    /// <summary>The body of the question, as text and path parts. <see cref="FolderRelation.Nested"/> has no question, only a warning.</summary>
    public static IReadOnlyList<StorageTextPart> Question(StorageFolderKind kind, string subject, string oldPath, string newPath, FolderRelation relation)
    {
        if (relation == FolderRelation.Nested)
            return [new StorageTextPart(NestedWarning)];

        var lead = kind == StorageFolderKind.Data
            ? Loc.F("Settings.Storage.Question.SubjectIsIn", subject)
            : Loc.F("Settings.Storage.Question.SubjectAreIn", subject);
        if (relation == FolderRelation.OtherDrive)
        {
            var tail = kind == StorageFolderKind.Models
                ? Loc.T("Settings.Storage.Question.OtherDriveModels")
                : Loc.T("Settings.Storage.Question.OtherDriveRecordings");
            return
            [
                new(lead),
                new(oldPath, true),
                new(Loc.T("Settings.Storage.Question.DifferentDrive")),
                new(newPath, true),
                new(tail),
            ];
        }

        var after = kind switch
        {
            StorageFolderKind.Models => Loc.T("Settings.Storage.Question.AfterModels"),
            StorageFolderKind.Recordings => Loc.T("Settings.Storage.Question.AfterRecordings"),
            _ => Loc.T("Settings.Storage.Question.AfterData"),
        };
        return
        [
            new(lead),
            new(oldPath, true),
            new(Loc.T("Settings.Storage.Question.NewFolder")),
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
        StorageFolderKind.Recordings => Loc.F("Settings.Storage.Recordings.PendingNote", target),
        _ => Loc.F("Settings.Storage.Data.PendingNote", target),
    };

    /// <summary>
    /// "Keep old location" for recordings: the history store opens once at start, so the new folder starts working after a restart.
    /// </summary>
    public static string RecordingsSwitchNote(string newFolder) =>
        Loc.F("Settings.Storage.Recordings.SwitchNote", newFolder);

    /// <summary>The note after a start-up move of recordings that could not finish.</summary>
    /// <remarks>Stays English: it is stored in settings.json (<see cref="AppSettings.SessionsStorageNote"/>) at start-up, before the interface language is known.</remarks>
    public static string RecordingsIncompleteNote(string wantedRoot, string actualRoot, IReadOnlyList<FolderMoveSkip> skipped)
    {
        var why = skipped.Any(s => s.Reason == ModelMoveSkipReason.OtherDrive) ? "it is on another drive and recordings are not copied automatically"
            : skipped.Any(s => s.Reason == ModelMoveSkipReason.InUse) ? "some files were in use"
            : skipped.Any(s => s.Reason == ModelMoveSkipReason.DestinationExists) ? "some folders with the same name already exist there"
            : "the folders overlap";
        return $"Recordings were not moved to {wantedRoot}: {why}. They stay in {actualRoot}.";
    }

    /// <summary>The note after a start-up move of app data that could not finish.</summary>
    /// <remarks>Stays English: it is written to the note file at start-up, before the interface language is known.</remarks>
    public static string DataIncompleteNote(string wantedRoot, string actualRoot, IReadOnlyList<FolderMoveSkip> skipped)
    {
        var why = skipped.Any(s => s.Reason == ModelMoveSkipReason.InUse) ? "some files were in use"
            : skipped.Any(s => s.Reason == ModelMoveSkipReason.DestinationExists) ? "some files with the same name already exist there"
            : "the folders overlap";
        return $"App data was not moved to {wantedRoot}: {why}. It stays in {actualRoot}.";
    }

    /// <summary>The note when the saved app data folder cannot be used and the default one is in use.</summary>
    public static string DataUnavailableNote(string wantedRoot, string defaultRoot) =>
        Loc.F("Settings.Storage.Data.UnavailableNote", wantedRoot, defaultRoot);

    /// <summary>The line under the three cards.</summary>
    public static string Footer(bool android) => android
        ? Loc.T("Settings.Storage.FooterAndroid")
        : Loc.T("Settings.Storage.FooterDesktop");
}
