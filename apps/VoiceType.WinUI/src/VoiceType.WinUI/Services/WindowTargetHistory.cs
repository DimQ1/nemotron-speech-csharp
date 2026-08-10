using System.Text.Json;

namespace VoiceType.WinUI.Services;

/// <summary>Persistent, per-process LRU journal of injection windows and fields.</summary>
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

    public void Record(WindowTargetInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ProcessName))
            return;

        var existing = _targets.FirstOrDefault(target => SameTarget(target, info));
        if (existing is not null)
        {
            _targets.Remove(existing);
            _targets.Add(existing with
            {
                LastUsed = DateTimeOffset.Now,
                UseCount = existing.UseCount + 1,
            });
        }
        else
        {
            _targets.Add(new WindowTarget(
                info.ProcessName,
                info.WindowTitle,
                info.AutomationId,
                info.ControlType,
                info.ElementName,
                DateTimeOffset.Now,
                1));
        }

        TrimProcess(info.ProcessName);
        Save();
    }

    public void MarkUsed(WindowTarget target)
    {
        var existing = _targets.FirstOrDefault(candidate => SameTarget(candidate, target));
        if (existing is null)
            return;

        _targets.Remove(existing);
        _targets.Add(existing with
        {
            LastUsed = DateTimeOffset.Now,
            UseCount = existing.UseCount + 1,
        });
        Save();
    }

    public IReadOnlyList<WindowTarget> FindCandidates(
        string spokenAppName,
        IReadOnlyDictionary<string, string[]> aliases)
    {
        if (string.IsNullOrWhiteSpace(spokenAppName))
            return Array.Empty<WindowTarget>();

        var needle = spokenAppName.Trim();
        return _targets
            .Where(target => MatchesProcess(target.ProcessName, needle, aliases))
            .OrderByDescending(target => target.LastUsed)
            .ToList();
    }

    public void Clear()
    {
        _targets.Clear();
        Save();
    }

    private static bool MatchesProcess(
        string processName,
        string spokenName,
        IReadOnlyDictionary<string, string[]> aliases)
    {
        var process = processName.Trim();
        var needle = spokenName.Trim();
        if (process.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || needle.Contains(process, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var aliasPair = aliases.FirstOrDefault(pair =>
            string.Equals(pair.Key, process, StringComparison.OrdinalIgnoreCase));
        return aliasPair.Value?.Any(alias =>
            !string.IsNullOrWhiteSpace(alias)
            && AliasMatches(needle, alias)) == true;
    }

    private static bool AliasMatches(string spokenName, string alias)
    {
        var spoken = Normalize(spokenName);
        var configured = Normalize(alias);
        if (spoken.Length == 0 || configured.Length == 0)
            return false;

        if (spoken.Contains(configured, StringComparison.Ordinal)
            || configured.Contains(spoken, StringComparison.Ordinal))
        {
            return true;
        }

        // Russian declension and ASR variations: "блокноте" should match
        // the configured generic alias "блокнот".
        var stemLength = Math.Min(spoken.Length, configured.Length) - 2;
        return stemLength >= 4
            && string.Equals(spoken[..stemLength], configured[..stemLength], StringComparison.Ordinal);
    }

    private static string Normalize(string value) => new(value
        .ToLowerInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());

    private static bool SameTarget(WindowTarget target, WindowTargetInfo info) =>
        string.Equals(target.ProcessName, info.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(target.AutomationId, info.AutomationId, StringComparison.Ordinal)
        && string.Equals(target.ElementName, info.ElementName, StringComparison.Ordinal);

    private static bool SameTarget(WindowTarget left, WindowTarget right) =>
        string.Equals(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.AutomationId, right.AutomationId, StringComparison.Ordinal)
        && string.Equals(left.ElementName, right.ElementName, StringComparison.Ordinal);

    private void TrimProcess(string processName)
    {
        var processTargets = _targets
            .Where(target => string.Equals(target.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(target => target.LastUsed)
            .ToList();

        foreach (var stale in processTargets.Skip(_perProcessCap))
            _targets.Remove(stale);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var items = JsonSerializer.Deserialize<List<WindowTarget>>(File.ReadAllText(_filePath));
            if (items is not null)
                _targets.AddRange(items);
        }
        catch
        {
            // Corrupted history is non-fatal; start with an empty journal.
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(
                _targets,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Target history is best-effort and must not interrupt dictation.
        }
    }
}