using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace VoiceType.Uno.History;

/// <summary>A recording folder and its manifest (a caller-owned copy).</summary>
public sealed record RecordingEntry(string FolderPath, RecordingManifest Manifest)
{
    public string Id => Manifest.Id;
}

/// <summary>A folder that could not be loaded; reported, never thrown.</summary>
public sealed record HistoryLoadError(string Path, string Message);

public sealed record RecordingLoadResult(IReadOnlyList<RecordingEntry> Recordings, IReadOnlyList<HistoryLoadError> Errors);

public sealed record VersionDeletion(string VersionId, IReadOnlyList<string> DeletedTranslationIds);

public sealed record RecoveryResult(
    IReadOnlyList<string> Recovered,
    IReadOnlyList<string> MarkedIncomplete,
    IReadOnlyList<HistoryLoadError> Errors);

/// <summary>
/// Single writer of the recording folders under <c>sessions/</c>:
/// <code>
/// &lt;sessions&gt;/2026-10-12_14-02-11_7f3a9c/
///   manifest.json   transcripts/vN.json   translations/tN.&lt;lang&gt;.&lt;ver&gt;.json
///   audio.ogg (audio.ogg.part while recording)   .lock (while recording)
/// </code>
/// Every JSON write is atomic (temp file + move) and every mutation is a
/// load-modify-write under one lock, so concurrent callers never lose a change.
/// Returned manifests are copies; mutate only through this type.
/// </summary>
public sealed class RecordingStore
{
    public const string ManifestFileName = "manifest.json";
    public const string LockFileName = ".lock";
    public const string TranscriptsFolder = "transcripts";
    public const string TranslationsFolder = "translations";
    public const string PartSuffix = ".part";
    private const string FolderTimeFormat = "yyyy-MM-dd_HH-mm-ss";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public RecordingStore(string sessionsDir, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsDir);
        SessionsDir = sessionsDir;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string SessionsDir { get; }

    /// <summary>Raised (outside the lock) with the recording id after any change.</summary>
    public event Action<string>? Changed;

    // ── Create / finish ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates <c>&lt;yyyy-MM-dd_HH-mm-ss&gt;_&lt;6hex&gt;/</c> (local start time) with a
    /// <c>.lock</c> file and a manifest in state <see cref="RecordingStates.Recording"/>.
    /// </summary>
    public RecordingEntry CreateRecording(DateTimeOffset startedAt, RecordingMode? mode = null, AudioInfo? audio = null)
    {
        RecordingEntry entry;
        lock (_gate)
        {
            Directory.CreateDirectory(SessionsDir);
            var id = NewId();
            var folder = Path.Combine(SessionsDir, $"{startedAt.ToString(FolderTimeFormat, CultureInfo.InvariantCulture)}_{id}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, LockFileName), "");

            var manifest = new RecordingManifest
            {
                Id = id,
                StartedAt = startedAt,
                State = RecordingStates.Recording,
                Mode = mode ?? new RecordingMode(),
                Audio = audio,
            };
            WriteManifest(folder, manifest);
            _folders[id] = folder;
            entry = new RecordingEntry(folder, Copy(manifest));
        }
        Changed?.Invoke(entry.Id);
        return entry;
    }

    /// <summary>Marks the recording complete and removes <c>.lock</c>.</summary>
    public RecordingManifest FinishRecording(string id, DateTimeOffset endedAt, AudioInfo? audio = null)
    {
        return Mutate(id, (folder, m) =>
        {
            m.EndedAt = endedAt;
            m.State = RecordingStates.Complete;
            if (audio is not null)
                m.Audio = audio;
            DeleteFileQuietly(Path.Combine(folder, LockFileName));
        });
    }

    // ── Read ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads every recording folder, newest first. A corrupted or foreign folder is
    /// reported in <see cref="RecordingLoadResult.Errors"/> and skipped.
    /// </summary>
    public RecordingLoadResult LoadAll()
    {
        var recordings = new List<RecordingEntry>();
        var errors = new List<HistoryLoadError>();
        if (!Directory.Exists(SessionsDir))
            return new RecordingLoadResult(recordings, errors);

        lock (_gate)
        {
            _folders.Clear();
            foreach (var folder in Directory.EnumerateDirectories(SessionsDir))
            {
                var manifestPath = Path.Combine(folder, ManifestFileName);
                try
                {
                    if (!File.Exists(manifestPath))
                    {
                        errors.Add(new HistoryLoadError(folder, "manifest.json is missing."));
                        continue;
                    }
                    var manifest = HistoryJson.Read(manifestPath, HistoryJsonContext.Default.RecordingManifest);
                    if (string.IsNullOrEmpty(manifest.Id))
                        manifest.Id = IdFromFolderName(folder) ?? "";
                    if (string.IsNullOrEmpty(manifest.Id) || _folders.ContainsKey(manifest.Id))
                    {
                        errors.Add(new HistoryLoadError(folder, $"Missing or duplicate recording id '{manifest.Id}'."));
                        continue;
                    }
                    _folders[manifest.Id] = folder;
                    recordings.Add(new RecordingEntry(folder, manifest));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    errors.Add(new HistoryLoadError(folder, ex.Message));
                }
            }
        }

        recordings.Sort((x, y) => y.Manifest.StartedAt.CompareTo(x.Manifest.StartedAt));
        return new RecordingLoadResult(recordings, errors);
    }

    public RecordingEntry? Get(string id)
    {
        lock (_gate)
        {
            var folder = TryGetFolderCore(id);
            if (folder is null)
                return null;
            try
            {
                return new RecordingEntry(folder, ReadManifest(folder));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }
    }

    public string? TryGetFolder(string id)
    {
        lock (_gate)
            return TryGetFolderCore(id);
    }

    public TranscriptDocument LoadTranscript(string id, string versionId)
    {
        lock (_gate)
        {
            var folder = RequireFolder(id);
            var manifest = ReadManifest(folder);
            var version = manifest.FindVersion(versionId)
                ?? throw new KeyNotFoundException($"Recording {id} has no version {versionId}.");
            return HistoryJson.Read(Path.Combine(folder, version.File), HistoryJsonContext.Default.TranscriptDocument);
        }
    }

    public TranscriptDocument? LoadCurrentTranscript(string id)
    {
        var entry = Get(id);
        var current = entry?.Manifest.CurrentVersion;
        return current is null ? null : LoadTranscript(id, current);
    }

    public TranslationDocument LoadTranslation(string id, string translationId)
    {
        lock (_gate)
        {
            var folder = RequireFolder(id);
            var info = ReadManifest(folder).FindTranslation(translationId)
                ?? throw new KeyNotFoundException($"Recording {id} has no translation {translationId}.");
            return HistoryJson.Read(Path.Combine(folder, info.File), HistoryJsonContext.Default.TranslationDocument);
        }
    }

    /// <summary>Full path of the audio file, or null when there is none on disk.</summary>
    public string? GetAudioPath(string id)
    {
        var entry = Get(id);
        if (entry?.Manifest.Audio is not { } audio || string.IsNullOrEmpty(audio.File))
            return null;
        var path = Path.Combine(entry.FolderPath, audio.File);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Bytes of audio on disk (final and partial files).</summary>
    public static long MeasureAudioBytes(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return 0;
        return Directory.EnumerateFiles(folderPath, "audio.*").Sum(f => new FileInfo(f).Length);
    }

    // ── Generic mutation ────────────────────────────────────────────────────

    /// <summary>Load-modify-write of one manifest under the store lock.</summary>
    public RecordingManifest Update(string id, Action<RecordingManifest> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return Mutate(id, (_, m) => mutate(m));
    }

    /// <summary>Sets the user title; null/blank clears it (the auto/fallback title shows again).</summary>
    public RecordingManifest Rename(string id, string? title) =>
        Mutate(id, (_, m) => m.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim());

    /// <summary>Stores the generated title. The user title, if any, keeps precedence.</summary>
    public RecordingManifest SetAutoTitle(string id, string? autoTitle) =>
        Mutate(id, (_, m) => m.AutoTitle = string.IsNullOrWhiteSpace(autoTitle) ? null : autoTitle.Trim());

    // ── Versions ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="document"/> as a new immutable version. <paramref name="spec"/>
    /// supplies kind, model, language, settings, app, parent and timing stats; the store
    /// assigns id, file, creation time, word/segment counts and the excerpt.
    /// </summary>
    public TranscriptVersion AddVersion(string id, TranscriptVersion spec, TranscriptDocument document, bool makeCurrent = true)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(document);

        TranscriptVersion? added = null;
        Mutate(id, (folder, m) =>
        {
            var version = HistoryJson.Clone(spec, VersionTypeInfo);
            version.Id = NextId(m.Versions.Select(v => v.Id), "v");
            version.File = $"{TranscriptsFolder}/{version.Id}.json";
            version.CreatedAt = _time.GetLocalNow();
            version.Stats ??= new VersionStats();
            version.Stats.Words = document.WordCount;
            version.Stats.Segments = document.Segments.Count;

            var doc = HistoryJson.Clone(document, HistoryJsonContext.Default.TranscriptDocument);
            doc.Version = version.Id;
            if (string.IsNullOrEmpty(doc.Text))
                doc.Text = TranscriptDocument.JoinSegments(doc.Segments);
            version.Excerpt = Excerpt(doc.Text);

            if (version.Parent is not null && m.FindVersion(version.Parent) is null)
                throw new KeyNotFoundException($"Parent version {version.Parent} does not exist.");

            HistoryJson.WriteAtomic(Path.Combine(folder, version.File), doc, HistoryJsonContext.Default.TranscriptDocument);
            m.Versions.Add(version);
            if (makeCurrent || m.CurrentVersion is null)
                m.CurrentVersion = version.Id;
            added = version;
        });
        return HistoryJson.Clone(added!, VersionTypeInfo);
    }

    /// <summary>
    /// Saves a per-segment edit of <paramref name="sourceVersionId"/> as a new version of
    /// kind <see cref="VersionKinds.Edited"/> (parent = source) and makes it current.
    /// Timings of unchanged words are carried over.
    /// </summary>
    public TranscriptVersion SaveEdit(string id, string sourceVersionId, IReadOnlyList<string> segmentTexts) =>
        SaveEditCore(id, sourceVersionId, (source, newId) => TranscriptTimings.CarryOver(source, segmentTexts, newId));

    /// <summary>Whole-text variant of <see cref="SaveEdit(string,string,IReadOnlyList{string})"/>.</summary>
    public TranscriptVersion SaveEdit(string id, string sourceVersionId, string fullText) =>
        SaveEditCore(id, sourceVersionId, (source, newId) => TranscriptTimings.CarryOver(source, fullText, newId));

    private TranscriptVersion SaveEditCore(
        string id, string sourceVersionId, Func<TranscriptDocument, string, TranscriptDocument> build)
    {
        lock (_gate)
        {
            var source = LoadTranscript(id, sourceVersionId);
            var manifest = ReadManifest(RequireFolder(id));
            var parent = manifest.FindVersion(sourceVersionId)!;

            var edited = build(source, "");
            var spec = HistoryJson.Clone(parent, VersionTypeInfo);
            spec.Kind = VersionKinds.Edited;
            spec.Parent = sourceVersionId;
            spec.Edited = false;
            spec.ExtensionData = null;
            spec.Stats = new VersionStats();
            return AddVersion(id, spec, edited, makeCurrent: true);
        }
    }

    public RecordingManifest SetCurrentVersion(string id, string versionId) =>
        Mutate(id, (_, m) =>
        {
            if (m.FindVersion(versionId) is null)
                throw new KeyNotFoundException($"Recording {id} has no version {versionId}.");
            m.CurrentVersion = versionId;
        });

    /// <summary>Translations that would be deleted together with <paramref name="versionId"/>.</summary>
    public IReadOnlyList<TranslationInfo> GetDependentTranslations(string id, string versionId)
    {
        var entry = Get(id) ?? throw new KeyNotFoundException($"Recording {id} not found.");
        return entry.Manifest.Translations.Where(t => t.SourceVersion == versionId).ToList();
    }

    /// <summary>
    /// Deletes a non-current version and the translations made from it. Chats that cite
    /// the version are the caller's business (<see cref="ChatStore.ListReferencing"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The version is the current one.</exception>
    public VersionDeletion DeleteVersion(string id, string versionId)
    {
        var deleted = new List<string>();
        Mutate(id, (folder, m) =>
        {
            var version = m.FindVersion(versionId)
                ?? throw new KeyNotFoundException($"Recording {id} has no version {versionId}.");
            if (m.CurrentVersion == versionId)
                throw new InvalidOperationException("The current version cannot be deleted; make another version current first.");

            foreach (var t in m.Translations.Where(t => t.SourceVersion == versionId).ToList())
            {
                DeleteFileQuietly(Path.Combine(folder, t.File));
                m.Translations.Remove(t);
                deleted.Add(t.Id);
            }
            // Edited children keep their own full copy of the text; only the link goes.
            foreach (var child in m.Versions.Where(v => v.Parent == versionId))
                child.Parent = null;

            m.Versions.Remove(version);
            DeleteFileQuietly(Path.Combine(folder, version.File));
            // Gemma analysis caches of the version (summarize job).
            DeleteFileQuietly(Path.Combine(folder, AnalysisStore.ChunksFile(versionId)));
            DeleteFileQuietly(Path.Combine(folder, AnalysisStore.SummaryFile(versionId)));
            DeleteFileQuietly(Path.Combine(folder, AnalysisStore.LegacyChatSummariesFile(versionId)));
        });
        return new VersionDeletion(versionId, deleted);
    }

    // ── Translations ────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a translation (when <paramref name="info"/>.Id is empty) or replaces an
    /// existing one (resume of a partial translation). Returns the stored info.
    /// </summary>
    public TranslationInfo SaveTranslation(string id, TranslationInfo info, TranslationDocument document)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(document);

        TranslationInfo? saved = null;
        Mutate(id, (folder, m) =>
        {
            if (m.FindVersion(info.SourceVersion) is null)
                throw new KeyNotFoundException($"Recording {id} has no version {info.SourceVersion}.");

            var stored = HistoryJson.Clone(info, TranslationTypeInfo);
            var existing = string.IsNullOrEmpty(info.Id) ? null : m.FindTranslation(info.Id);
            if (existing is null)
            {
                stored.Id = string.IsNullOrEmpty(info.Id) ? NextId(m.Translations.Select(t => t.Id), "t") : info.Id;
                stored.File = $"{TranslationsFolder}/{stored.Id}.{SafeName(stored.TargetLanguage)}.{stored.SourceVersion}.json";
                if (stored.CreatedAt == default)
                    stored.CreatedAt = _time.GetLocalNow();
                m.Translations.Add(stored);
            }
            else
            {
                stored.File = existing.File;
                stored.CreatedAt = existing.CreatedAt;
                m.Translations[m.Translations.IndexOf(existing)] = stored;
            }

            var doc = HistoryJson.Clone(document, HistoryJsonContext.Default.TranslationDocument);
            doc.SourceVersion = stored.SourceVersion;
            doc.TargetLanguage = stored.TargetLanguage;
            HistoryJson.WriteAtomic(Path.Combine(folder, stored.File), doc, HistoryJsonContext.Default.TranslationDocument);
            saved = stored;
        });
        return HistoryJson.Clone(saved!, TranslationTypeInfo);
    }

    public RecordingManifest DeleteTranslation(string id, string translationId) =>
        Mutate(id, (folder, m) =>
        {
            var t = m.FindTranslation(translationId)
                ?? throw new KeyNotFoundException($"Recording {id} has no translation {translationId}.");
            DeleteFileQuietly(Path.Combine(folder, t.File));
            m.Translations.Remove(t);
        });

    // ── Delete ──────────────────────────────────────────────────────────────

    /// <summary>Deletes the whole recording folder (text, audio, translations).</summary>
    public void DeleteRecording(string id)
    {
        lock (_gate)
        {
            var folder = RequireFolder(id);
            Directory.Delete(folder, recursive: true);
            _folders.Remove(id);
        }
        Changed?.Invoke(id);
    }

    /// <summary>
    /// Deletes the audio files only; text, versions, translations stay. Sets
    /// <see cref="AudioInfo.RemovedAt"/>. Returns false when there was no audio.
    /// </summary>
    public bool DeleteAudio(string id)
    {
        var removed = false;
        Mutate(id, (folder, m) =>
        {
            foreach (var file in Directory.EnumerateFiles(folder, "audio.*"))
            {
                DeleteFileQuietly(file);
                removed = true;
            }
            if (m.Audio is not null && m.Audio.RemovedAt is null)
            {
                m.Audio.RemovedAt = _time.GetLocalNow();
                removed = true;
            }
        });
        return removed;
    }

    // ── Recovery ────────────────────────────────────────────────────────────

    /// <summary>
    /// Finalizes recordings left unfinished by a crash: for every folder (not in
    /// <paramref name="activeIds"/>) with a <c>.lock</c>, an <c>audio.*.part</c> or state
    /// <see cref="RecordingStates.Recording"/>, the partial audio is finalized
    /// (<paramref name="finalizeAudio"/>(partPath, finalPath) — e.g. close the Ogg stream;
    /// default renames), the lock is removed and the state becomes
    /// <see cref="RecordingStates.Recovered"/> (or <see cref="RecordingStates.Incomplete"/>
    /// when neither audio nor a transcript survived). A folder with partial files but
    /// no manifest gets a minimal one. Corrupted manifests are reported, not touched.
    /// </summary>
    public RecoveryResult RecoverIncomplete(
        IReadOnlySet<string>? activeIds = null,
        Func<string, string, bool>? finalizeAudio = null)
    {
        var recovered = new List<string>();
        var incomplete = new List<string>();
        var errors = new List<HistoryLoadError>();
        if (!Directory.Exists(SessionsDir))
            return new RecoveryResult(recovered, incomplete, errors);

        lock (_gate)
        {
            foreach (var folder in Directory.EnumerateDirectories(SessionsDir))
            {
                try
                {
                    var outcome = RecoverFolder(folder, activeIds, finalizeAudio);
                    if (outcome == RecordingStates.Recovered) recovered.Add(Path.GetFileName(folder));
                    else if (outcome == RecordingStates.Incomplete) incomplete.Add(Path.GetFileName(folder));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    errors.Add(new HistoryLoadError(folder, ex.Message));
                }
            }
        }
        return new RecoveryResult(recovered, incomplete, errors);
    }

    private string? RecoverFolder(string folder, IReadOnlySet<string>? activeIds, Func<string, string, bool>? finalizeAudio)
    {
        var manifestPath = Path.Combine(folder, ManifestFileName);
        var lockPath = Path.Combine(folder, LockFileName);
        var parts = Directory.EnumerateFiles(folder, "audio.*" + PartSuffix).ToList();

        RecordingManifest manifest;
        if (File.Exists(manifestPath))
        {
            manifest = HistoryJson.Read(manifestPath, HistoryJsonContext.Default.RecordingManifest);
            if (string.IsNullOrEmpty(manifest.Id))
                manifest.Id = IdFromFolderName(folder) ?? "";
        }
        else
        {
            var folderId = IdFromFolderName(folder);
            if (folderId is null || (parts.Count == 0 && !File.Exists(lockPath)))
                return null; // not ours / nothing to recover; LoadAll reports it
            manifest = new RecordingManifest
            {
                Id = folderId,
                StartedAt = StartFromFolderName(folder) ?? new DateTimeOffset(Directory.GetCreationTime(folder)),
                State = RecordingStates.Recording,
            };
        }

        if (activeIds is not null && activeIds.Contains(manifest.Id))
            return null;
        if (manifest.State != RecordingStates.Recording && parts.Count == 0 && !File.Exists(lockPath))
            return null;

        foreach (var part in parts)
        {
            var final = part[..^PartSuffix.Length];
            if (File.Exists(final))
            {
                DeleteFileQuietly(part); // the final file was already written
                continue;
            }
            var ok = finalizeAudio?.Invoke(part, final) ?? TryMove(part, final);
            if (!ok || !File.Exists(final))
                continue;

            manifest.Audio ??= new AudioInfo();
            manifest.Audio.File = Path.GetFileName(final);
            manifest.Audio.Codec ??= CodecFromExtension(final);
            manifest.Audio.Bytes = new FileInfo(final).Length;
            manifest.Audio.RemovedAt = null;
        }

        var hasAudio = manifest.Audio is { } a && !string.IsNullOrEmpty(a.File) && File.Exists(Path.Combine(folder, a.File));
        var hasText = manifest.Versions.Count > 0;
        manifest.State = hasAudio || hasText ? RecordingStates.Recovered : RecordingStates.Incomplete;
        manifest.EndedAt ??= LastWriteTime(folder);

        WriteManifest(folder, manifest);
        DeleteFileQuietly(lockPath);
        _folders[manifest.Id] = folder;
        return manifest.State;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<TranscriptVersion> VersionTypeInfo =>
        HistoryJsonContext.Default.TranscriptVersion;

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<TranslationInfo> TranslationTypeInfo =>
        HistoryJsonContext.Default.TranslationInfo;

    private RecordingManifest Mutate(string id, Action<string, RecordingManifest> mutate)
    {
        RecordingManifest copy;
        lock (_gate)
        {
            var folder = RequireFolder(id);
            var manifest = ReadManifest(folder);
            mutate(folder, manifest);
            WriteManifest(folder, manifest);
            copy = Copy(manifest);
        }
        Changed?.Invoke(id);
        return copy;
    }

    private string RequireFolder(string id) =>
        TryGetFolderCore(id) ?? throw new KeyNotFoundException($"Recording {id} not found.");

    private string? TryGetFolderCore(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (_folders.TryGetValue(id, out var known) && Directory.Exists(known))
            return known;
        if (!Directory.Exists(SessionsDir))
            return null;
        var found = Directory.EnumerateDirectories(SessionsDir, "*_" + id).FirstOrDefault();
        if (found is not null)
            _folders[id] = found;
        return found;
    }

    private static RecordingManifest ReadManifest(string folder) =>
        HistoryJson.Read(Path.Combine(folder, ManifestFileName), HistoryJsonContext.Default.RecordingManifest);

    private static void WriteManifest(string folder, RecordingManifest manifest) =>
        HistoryJson.WriteAtomic(Path.Combine(folder, ManifestFileName), manifest, HistoryJsonContext.Default.RecordingManifest);

    private static RecordingManifest Copy(RecordingManifest manifest) =>
        HistoryJson.Clone(manifest, HistoryJsonContext.Default.RecordingManifest);

    private string NewId()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            if (TryGetFolderCore(id) is null)
                return id;
        }
        throw new IOException("Could not allocate a unique recording id.");
    }

    /// <summary><c>prefix</c> + (largest numeric suffix + 1), e.g. v1, v2 → v3.</summary>
    internal static string NextId(IEnumerable<string> existing, string prefix)
    {
        var max = 0;
        foreach (var id in existing)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                max = Math.Max(max, n);
        }
        return prefix + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    internal static string? IdFromFolderName(string folder)
    {
        var name = Path.GetFileName(folder);
        var underscore = name.LastIndexOf('_');
        if (underscore < 0 || name.Length - underscore - 1 != 6)
            return null;
        var id = name[(underscore + 1)..];
        return id.All(Uri.IsHexDigit) ? id : null;
    }

    private static DateTimeOffset? StartFromFolderName(string folder)
    {
        var name = Path.GetFileName(folder);
        if (name.Length < FolderTimeFormat.Length)
            return null;
        return DateTime.TryParseExact(name[..FolderTimeFormat.Length], FolderTimeFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local)
            : null;
    }

    private static DateTimeOffset LastWriteTime(string folder)
    {
        var latest = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(File.GetLastWriteTime)
            .DefaultIfEmpty(Directory.GetLastWriteTime(folder))
            .Max();
        return new DateTimeOffset(latest);
    }

    private static string? CodecFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ogg" or ".opus" => "opus",
        ".wav" => "pcm16",
        ".mp3" => "mp3",
        _ => null,
    };

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Excerpt(string text)
    {
        const int max = 200;
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string SafeName(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray();
        return chars.Length == 0 ? "xx" : new string(chars);
    }

    internal static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
