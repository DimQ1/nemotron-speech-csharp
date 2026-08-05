using System.IO;
using System.Text.Json;

namespace VoiceType.Services;

/// <summary>One remembered injection target (window + optional focused field).</summary>
public sealed record WindowTarget(
    string ProcessName,
    string WindowTitle,
    string? AutomationId,
    string? ControlType,
    string? ElementName,
    DateTimeOffset LastUsed,
    int UseCount)
{
    public string DisplayLabel
    {
        get
        {
            var fieldName = ElementName ?? AutomationId ?? ControlType;
            return string.IsNullOrWhiteSpace(fieldName)
                ? $"{ProcessName} — {WindowTitle}"
                : $"{ProcessName} — {fieldName}";
        }
    }
}

/// <summary>
/// Persistent per-process journal of injection targets (windows / input fields).
/// Stored as local JSON only (privacy), LRU-trimmed to a configurable per-process
/// cap, clearable from settings. Powers "type into &lt;app&gt;" disambiguation.
/// </summary>
public sealed class WindowTargetHistory
{
    private readonly string _filePath;
    private readonly int _perProcessCap;
    private readonly List<WindowTarget> _targets = new();

    public WindowTargetHistory(string filePath, int perProcessCap = 10)
    {
        _filePath = filePath;
        _perProcessCap = Math.Max(1, perProcessCap);
        Load();
    }

    public IReadOnlyList<WindowTarget> Targets => _targets;

    /// <summary>Record a successful injection target snapshot.</summary>
    public void Record(WindowTargetInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ProcessName))
            return;

        var existing = _targets.FirstOrDefault(t => SameTarget(t, info));
        if (existing is not null)
        {
            _targets.Remove(existing);
            _targets.Add(existing with { LastUsed = DateTimeOffset.Now, UseCount = existing.UseCount + 1 });
        }
        else
        {
            _targets.Add(new WindowTarget(
                info.ProcessName, info.WindowTitle, info.AutomationId,
                info.ControlType, info.ElementName, DateTimeOffset.Now, 1));
        }

        TrimProcess(info.ProcessName);
        Save();
    }

    /// <summary>
    /// Find remembered targets for a spoken app name. Matches the process name
    /// directly and any configured alias. Ordered by most-recently used.
    /// </summary>
    public IReadOnlyList<WindowTarget> FindCandidates(string spokenAppName, IReadOnlyDictionary<string, string[]> aliases)
    {
        if (string.IsNullOrWhiteSpace(spokenAppName))
            return Array.Empty<WindowTarget>();

        var needle = spokenAppName.Trim().ToLowerInvariant();

        bool MatchesProcess(string processName)
        {
            var proc = processName.ToLowerInvariant();
            if (proc.Contains(needle, StringComparison.Ordinal) || needle.Contains(proc, StringComparison.Ordinal))
                return true;
            if (aliases.TryGetValue(proc, out var names))
                return names.Any(a => needle.Contains(a.ToLowerInvariant(), StringComparison.Ordinal));
            return false;
        }

        return _targets
            .Where(t => MatchesProcess(t.ProcessName))
            .OrderByDescending(t => t.LastUsed)
            .ToList();
    }

    /// <summary>Remove all remembered targets (privacy / reset from settings).</summary>
    public void Clear()
    {
        _targets.Clear();
        Save();
    }

    private static bool SameTarget(WindowTarget t, WindowTargetInfo info)
        => string.Equals(t.ProcessName, info.ProcessName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(t.AutomationId, info.AutomationId, StringComparison.Ordinal)
           && string.Equals(t.ElementName, info.ElementName, StringComparison.Ordinal);

    private void TrimProcess(string processName)
    {
        var forProcess = _targets
            .Where(t => string.Equals(t.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.LastUsed)
            .ToList();

        foreach (var stale in forProcess.Skip(_perProcessCap))
            _targets.Remove(stale);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var items = JsonSerializer.Deserialize<List<WindowTarget>>(File.ReadAllText(_filePath));
                if (items is not null)
                    _targets.AddRange(items);
            }
        }
        catch { /* corrupted file → start empty */ }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath,
                JsonSerializer.Serialize(_targets, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* history persistence is best-effort */ }
    }
}
