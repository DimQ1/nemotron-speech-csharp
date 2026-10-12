using System.Globalization;

namespace VoiceType.Uno.History;

/// <summary>Bytes used by the history on disk.</summary>
/// <param name="AudioBytes">All <c>audio.*</c> files of the recording folders.</param>
/// <param name="TextBytes">Everything else: manifests, transcripts, translations, chats.</param>
public readonly record struct HistoryStorageUsage(long AudioBytes, long TextBytes, int Recordings)
{
    public long TotalBytes => AudioBytes + TextBytes;
}

/// <summary>A choice of a Settings › History picker.</summary>
public sealed record HistoryUiOption<T>(T Value, string Label, string? Detail = null);

/// <summary>Storage measurement and the History settings choices / texts.</summary>
public static class HistoryUiStorage
{
    public static readonly IReadOnlyList<HistoryUiOption<int>> RetentionChoices =
        HistorySettings.RetentionDayChoices.Select(d => new HistoryUiOption<int>(d, RetentionLabel(d))).ToList();

    /// <summary>0.5 · 1 · 2 · 5 · 10 GB · No limit (MB values, 0 = no limit).</summary>
    public static readonly IReadOnlyList<HistoryUiOption<int>> StorageCapChoices =
    [
        new(512, "0.5 GB"),
        new(1024, "1 GB"),
        new(2048, "2 GB"),
        new(5120, "5 GB"),
        new(10240, "10 GB"),
        new(0, "No limit"),
    ];

    public static readonly IReadOnlyList<HistoryUiOption<AudioQuality>> QualityChoices =
    [
        new(AudioQuality.Standard, "Standard", "Ogg Opus 48 kbps · ~19.5 MB per hour"),
        new(AudioQuality.High, "High", "WAV · ~115 MB per hour · lossless re-recognition"),
        new(AudioQuality.Compact, "Compact", "Ogg Opus 24 kbps · ~10 MB per hour"),
    ];

    public static string RetentionLabel(int days) => days switch
    {
        <= 0 => "Forever",
        365 => "1 year",
        1 => "1 day",
        _ => days.ToString(CultureInfo.InvariantCulture) + " days",
    };

    public static string StorageCapLabel(int mb) =>
        StorageCapChoices.FirstOrDefault(c => c.Value == mb)?.Label
        ?? (mb <= 0 ? "No limit" : HistoryUiFormat.Bytes(mb * 1024L * 1024L));

    /// <summary>Index of <paramref name="value"/> in <paramref name="choices"/>; the list is extended with a custom value when missing.</summary>
    public static int IndexOf<T>(ref IReadOnlyList<HistoryUiOption<T>> choices, T value, Func<T, string> label)
    {
        for (var i = 0; i < choices.Count; i++)
            if (EqualityComparer<T>.Default.Equals(choices[i].Value, value))
                return i;
        choices = [.. choices, new HistoryUiOption<T>(value, label(value))];
        return choices.Count - 1;
    }

    /// <summary>Fill of the usage bar: audio share of the cap (0..1); with no cap, share of audio + free space.</summary>
    public static double UsageFraction(HistoryStorageUsage usage, HistorySettings settings, long? freeBytes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var cap = settings.MaxAudioStorageBytes;
        double denominator = cap > 0 ? cap : usage.AudioBytes + (freeBytes ?? 0);
        return denominator <= 0 ? 0 : Math.Clamp(usage.AudioBytes / denominator, 0, 1);
    }

    /// <summary><c>Audio 1.1 GB of 2 GB · text and chats 96 MB · 41 GB free on device</c>.</summary>
    public static string UsageText(HistoryStorageUsage usage, HistorySettings settings, long? freeBytes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var audio = "Audio " + HistoryUiFormat.Bytes(usage.AudioBytes)
            + (settings.MaxAudioStorageMb > 0 ? " of " + StorageCapLabel(settings.MaxAudioStorageMb) : "");
        var text = "text and chats " + HistoryUiFormat.Bytes(usage.TextBytes);
        var free = freeBytes is { } f and > 0 ? HistoryUiFormat.Bytes(f) + " free on device" : null;
        return HistoryUiFormat.Join(audio, text, free);
    }

    /// <summary>Storage card at the end of the list: title line.</summary>
    public static string CardTitle(HistoryStorageUsage usage, HistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return "Storage · " + HistoryUiFormat.Bytes(usage.TotalBytes)
            + (settings.MaxAudioStorageMb > 0 ? " of " + StorageCapLabel(settings.MaxAudioStorageMb) : "");
    }

    /// <summary>Storage card detail: <c>Audio kept 30 days, transcripts forever</c>.</summary>
    public static string CardDetail(HistorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var audio = settings.KeepAudioForever ? "Audio kept forever" : "Audio kept " + RetentionLabel(settings.AudioRetentionDays);
        return audio + ", transcripts forever";
    }

    /// <summary>Sums the sizes of the sessions and chats folders. Missing folders count as empty.</summary>
    public static HistoryStorageUsage Measure(string sessionsDir, string? chatsDir = null)
    {
        long audio = 0, text = 0;
        var recordings = 0;
        if (Directory.Exists(sessionsDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(sessionsDir))
            {
                recordings++;
                foreach (var file in SafeFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    var length = SafeLength(file);
                    if (name.StartsWith("audio.", StringComparison.OrdinalIgnoreCase))
                        audio += length;
                    else
                        text += length;
                }
            }
        }
        if (chatsDir is not null && Directory.Exists(chatsDir))
            text += SafeFiles(chatsDir).Sum(SafeLength);
        return new HistoryStorageUsage(audio, text, recordings);
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
