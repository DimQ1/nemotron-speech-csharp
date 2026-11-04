using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceType.Uno.Services;

/// <summary>What the import of VoiceType.WinUI data did.</summary>
/// <param name="SettingsImported">The WinUI <c>settings.json</c> was converted (the original is kept as <see cref="WinUiDataImport.BackupFileName"/>).</param>
/// <param name="DroppedSettings">WinUI values that could not be read into the Uno settings (their defaults are used).</param>
/// <param name="Models">The translation model moves.</param>
/// <param name="Note">The line Settings › Models shows when a model could not be brought over, or empty.</param>
public sealed record WinUiImportReport(bool SettingsImported, IReadOnlyList<string> DroppedSettings, ModelMigrationReport Models, string Note)
{
    public static WinUiImportReport Empty { get; } = new(false, [], ModelMigrationReport.Empty, "");
}

/// <summary>
/// One-time takeover of the data of VoiceType.WinUI when the Uno app replaces it in the Microsoft Store. Both packages have the
/// same identity (<c>DimQ1.VoiceType</c>), so after the update the Uno app sees the WinUI data folder as its own data root
/// (<c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local\VoiceType</c>) and nothing has to be copied. What differs is the layout:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>settings.json</c> has another schema (<c>TextInjectionMethod</c> is a number, which fails the Uno reader and would reset
/// every setting): it is converted once, the original stays as <see cref="BackupFileName"/>.</item>
/// <item>Speech models sit flat in <c>Models\</c> (one folder with the Uno <c>models\</c> on Windows) or in the WinUI custom models
/// folder, which becomes the Uno model storage folder; <see cref="ModelStorageMigrator"/> sorts them into <c>asr\</c> as it does for
/// older Uno versions.</item>
/// <item>The translation model is in <c>Models\Translation\</c>: it moves into <c>llm\</c> of the model storage folder (a rename on
/// one drive). On another drive it moves into <c>llm\</c> of the default folder and <see cref="WinUiImportReport.Note"/> says
/// where it is, because many GB are not copied at start-up.</item>
/// <item>The WinUI sessions (<c>Sessions\*.json</c>) are left alone; History lists only recording folders.</item>
/// </list>
/// Never overwrites, never deletes a file, safe to run at every start.
/// </remarks>
public static class WinUiDataImport
{
    public const string BackupFileName = "settings.winui.json";
    public const string TranslationFolderName = "Translation";

    /// <summary>Keys only VoiceType.WinUI writes; Uno settings files always have <c>History</c>.</summary>
    private static readonly string[] WinUiOnlyKeys =
        ["PostProcessingRules", "PostProcessingEnabled", "DownloaderModelsRootPath", "SaveSessions", "StopOnAnyInput", "MicVolume"];

    /// <summary>Settings converted by hand instead of copied.</summary>
    private static readonly string[] MappedKeys = ["TextInjectionMethod", "SessionsPath", "ModelsRootPath", "ModelPath"];

    /// <summary>Whether a <c>settings.json</c> was written by VoiceType.WinUI.</summary>
    public static bool IsWinUiSettings(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.ContainsKey("History"))
            return false;
        return json["TextInjectionMethod"] is JsonValue method && method.GetValueKind() == JsonValueKind.Number
            || WinUiOnlyKeys.Any(json.ContainsKey);
    }

    /// <summary>
    /// Start-up entry point, before <see cref="ModelStorageMigrator.RunAtStartup"/>: converts a WinUI <c>settings.json</c> in
    /// <paramref name="dataRoot"/> and moves <c>Models\Translation\</c> into the model storage folder.
    /// </summary>
    /// <param name="dataRoot">The data root (the folder of <c>settings.json</c>).</param>
    /// <param name="virtualDataRoot">
    /// The same folder as the packaged WinUI app may have stored it, <c>%LOCALAPPDATA%\VoiceType</c> (MSIX redirects it to
    /// <paramref name="dataRoot"/>); paths under it are rewritten to <paramref name="dataRoot"/>. Null when not packaged.
    /// </param>
    public static WinUiImportReport RunAtStartup(string dataRoot, string? virtualDataRoot = null, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var settingsPath = Path.Combine(dataRoot, "settings.json");
        var defaultModels = new ModelStorageLayout(Path.Combine(dataRoot, "models"));
        var service = new SettingsService(settingsPath);

        var imported = false;
        IReadOnlyList<string> dropped = [];
        AppSettings? settings = null;
        // Without a backup the original is not replaced: the next start tries again.
        if (ReadObject(settingsPath, log) is { } json && IsWinUiSettings(json) && BackUp(settingsPath, Path.Combine(dataRoot, BackupFileName), log))
        {
            (settings, dropped) = Convert(json, dataRoot, virtualDataRoot, defaultModels.Root, log);
            imported = true;
        }

        var translationDir = Path.Combine(defaultModels.Root, TranslationFolderName);
        if (!Directory.Exists(translationDir))
        {
            settings ??= File.Exists(settingsPath) ? service.Load() : null;
            if (settings is not null && (ClearResolvedNote(settings, defaultModels) || imported))
                service.Save(settings);
            return new WinUiImportReport(imported, dropped, ModelMigrationReport.Empty, "");
        }

        settings ??= service.Load();
        var target = string.IsNullOrWhiteSpace(settings.ModelsRootPath) ? defaultModels : new ModelStorageLayout(settings.ModelsRootPath);
        var models = ModelStorageMigrator.MoveInto(translationDir, target, ModelKind.Language, settings, log);
        var note = "";
        if (models.Skipped.Any(s => s.Reason == ModelMoveSkipReason.OtherDrive))
        {
            // Many GB are not copied at start-up: keep the model on its drive, in the Uno layout, and tell the user.
            var local = ModelStorageMigrator.MoveInto(translationDir, defaultModels, ModelKind.Language, settings, log);
            models = new ModelMigrationReport(
                [.. models.Moved, .. local.Moved],
                [.. models.Skipped.Where(s => s.Reason != ModelMoveSkipReason.OtherDrive), .. local.Skipped],
                models.SettingsChanged || local.SettingsChanged);
            if (local.Moved.Count > 0)
            {
                note = ModelStorageText.PreviousVersionModelNote(defaultModels.LlmDir, target.LlmDir);
                settings.ModelStorageNote = note;
            }
        }

        TryDeleteEmpty(translationDir);
        if (imported || models.SettingsChanged || note.Length > 0)
            service.Save(settings);
        log?.Invoke($"WinUI import: settings {(imported ? "converted" : "unchanged")}, translation model {models.Summary}");
        return new WinUiImportReport(imported, dropped, models, note);
    }

    /// <summary>
    /// The Uno settings for a WinUI <c>settings.json</c>: every value with the same name and a readable type is taken over;
    /// <c>TextInjectionMethod</c> (WinUI enum) becomes "type" or "paste"; the default models folder becomes the Uno default;
    /// the recordings folder is the Uno default (the WinUI sessions are a different format and stay where they are).
    /// </summary>
    internal static (AppSettings Settings, IReadOnlyList<string> Dropped) Convert(
        JsonObject json, string dataRoot, string? virtualDataRoot, string defaultModelsRoot, Action<string>? log = null)
    {
        var typeInfo = VoiceTypeJsonContext.Default.AppSettings;
        var known = typeInfo.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var accepted = new JsonObject();
        var dropped = new List<string>();
        foreach (var (name, value) in json)
        {
            if (!known.Contains(name) || MappedKeys.Contains(name))
                continue;

            var single = new JsonObject { [name] = value?.DeepClone() };
            try
            {
                if (single.Deserialize(typeInfo) is not null)
                    accepted[name] = value?.DeepClone();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                dropped.Add(name);
                log?.Invoke($"WinUI setting {name} not taken over: {ex.Message}");
            }
        }

        var settings = accepted.Deserialize(typeInfo) ?? new AppSettings();
        settings.TextInjectionMethod = IsClipboard(json["TextInjectionMethod"]) ? "paste" : "type";
        settings.SessionsPath = "";

        var modelsRoot = Rebase(StringOf(json["ModelsRootPath"]), dataRoot, virtualDataRoot);
        settings.ModelsRootPath = modelsRoot.Length == 0 || FolderMover.PathsEqual(modelsRoot, defaultModelsRoot) ? "" : modelsRoot;
        settings.ModelPath = Rebase(StringOf(json["ModelPath"]), dataRoot, virtualDataRoot);
        return (settings, dropped);
    }

    /// <summary>
    /// Drops the <see cref="ModelStorageText.PreviousVersionModelNote"/> once no <c>.litertlm</c> file is left in the default
    /// <c>llm\</c> folder it named (the user moved or deleted it). Other notes are not touched.
    /// </summary>
    private static bool ClearResolvedNote(AppSettings settings, ModelStorageLayout defaultModels)
    {
        if (!settings.ModelStorageNote.StartsWith(ModelStorageText.PreviousVersionModelNotePrefix, StringComparison.Ordinal))
            return false;
        try
        {
            if (Directory.Exists(defaultModels.LlmDir) && Directory.EnumerateFiles(defaultModels.LlmDir, "*.litertlm").Any())
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        settings.ModelStorageNote = "";
        return true;
    }

    /// <summary>WinUI <c>InjectionMethod.Clipboard</c> (2, or the name) is the Uno "paste"; InputSimulator and SendInput type.</summary>
    private static bool IsClipboard(JsonNode? node) => node is JsonValue value && value.GetValueKind() switch
    {
        JsonValueKind.Number => value.TryGetValue<int>(out var n) && n == 2,
        JsonValueKind.String => string.Equals(value.GetValue<string>(), "Clipboard", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static string StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>().Trim() : "";

    /// <summary>A path under the virtual <c>%LOCALAPPDATA%\VoiceType</c> as the same path under the real data root.</summary>
    private static string Rebase(string path, string dataRoot, string? virtualDataRoot)
    {
        if (path.Length == 0 || string.IsNullOrWhiteSpace(virtualDataRoot) || FolderMover.PathsEqual(virtualDataRoot, dataRoot))
            return path;
        try
        {
            var full = Path.GetFullPath(path);
            var from = Path.GetFullPath(virtualDataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (FolderMover.PathsEqual(full, from))
                return dataRoot;
            var prefix = from + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, ModelStorageLayout.PathComparison) ? Path.Combine(dataRoot, full[prefix.Length..]) : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return path;
        }
    }

    private static JsonObject? ReadObject(string path, Action<string>? log)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"cannot read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keeps the WinUI original; an earlier backup (an interrupted import) is never replaced.</summary>
    private static bool BackUp(string settingsPath, string backupPath, Action<string>? log)
    {
        try
        {
            if (!File.Exists(backupPath))
                File.Copy(settingsPath, backupPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"cannot back up {settingsPath}: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stays; the next start tries again.
        }
    }
}
