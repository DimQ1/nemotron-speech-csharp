using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

/// <summary>Builds a local, best-effort catalogue of possible voice-command destinations.</summary>
public sealed class VoiceCommandAppCatalog
{
    public IReadOnlyList<VoiceCommandApp> GetApplications(IReadOnlyList<VoiceCommandApp>? customApps)
    {
        var apps = new Dictionary<string, VoiceCommandApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in GetRunningApplications())
            Add(apps, app);

        foreach (var app in GetInstalledApplications())
            Add(apps, app);

        foreach (var app in GetAppPathApplications())
            Add(apps, app);

        foreach (var app in GetStartMenuApplications())
            Add(apps, app);

        foreach (var app in customApps ?? Array.Empty<VoiceCommandApp>())
            Add(apps, app);

        return apps.Values
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<VoiceCommandApp> FindCandidates(
        string spokenName,
        IReadOnlyList<VoiceCommandApp>? customApps,
        IReadOnlyDictionary<string, string[]>? aliases = null,
        int maxResults = 12)
    {
        var normalized = Normalize(spokenName);
        var applications = GetApplications(customApps)
            .Select(app => ApplyAliases(app, aliases))
            .Concat(GetAliasApplications(aliases))
            .GroupBy(app => Normalize(app.ProcessName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var candidates = applications
            .Select(app => new { App = app, Score = GetMatchScore(normalized, app) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxResults)
            .Select(candidate => candidate.App)
            .ToList();

        return candidates.Count > 0
            ? candidates
            : applications.Take(maxResults).ToList();
    }

    public bool Launch(VoiceCommandApp app)
    {
        if (string.IsNullOrWhiteSpace(app.ExecutablePath) || !File.Exists(app.ExecutablePath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(app.ExecutablePath) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Normalize(string value) => new(value
        .ToLowerInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());

    private static IEnumerable<VoiceCommandApp> GetRunningApplications()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (string.IsNullOrWhiteSpace(process.ProcessName))
                    continue;

                string executablePath = "";
                try { executablePath = process.MainModule?.FileName ?? ""; } catch { }

                yield return new VoiceCommandApp
                {
                    DisplayName = process.ProcessName,
                    ProcessName = process.ProcessName,
                    ExecutablePath = executablePath,
                };
            }
        }
    }

    private static IEnumerable<VoiceCommandApp> GetInstalledApplications()
    {
        var roots = new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, path) in roots)
        {
            using var root = hive.OpenSubKey(path);
            if (root is null)
                continue;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var displayName = key?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;

                var executablePath = ExtractExecutablePath(key?.GetValue("DisplayIcon") as string);
                var processName = executablePath.Length > 0
                    ? Path.GetFileNameWithoutExtension(executablePath)
                    : displayName;

                yield return new VoiceCommandApp
                {
                    DisplayName = displayName.Trim(),
                    ProcessName = processName,
                    ExecutablePath = executablePath,
                };
            }
        }
    }

    private static IEnumerable<VoiceCommandApp> GetAppPathApplications()
    {
        var roots = new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
        };

        foreach (var (hive, path) in roots)
        {
            using var root = hive.OpenSubKey(path);
            if (root is null)
                continue;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var executablePath = key?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(executablePath))
                    continue;

                executablePath = executablePath.Trim().Trim('"');
                yield return new VoiceCommandApp
                {
                    DisplayName = Path.GetFileNameWithoutExtension(name),
                    ProcessName = Path.GetFileNameWithoutExtension(name),
                    ExecutablePath = File.Exists(executablePath) ? executablePath : "",
                };
            }
        }
    }

    private static IEnumerable<VoiceCommandApp> GetStartMenuApplications()
    {
        var directories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var shortcut in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.AllDirectories))
            {
                var executablePath = TryResolveShortcut(shortcut);
                var displayName = Path.GetFileNameWithoutExtension(shortcut);
                yield return new VoiceCommandApp
                {
                    DisplayName = displayName,
                    ProcessName = executablePath.Length > 0 ? Path.GetFileNameWithoutExtension(executablePath) : displayName,
                    ExecutablePath = executablePath,
                };
            }
        }
    }

    private static void Add(IDictionary<string, VoiceCommandApp> apps, VoiceCommandApp app)
    {
        var key = Normalize(string.IsNullOrWhiteSpace(app.ProcessName) ? app.DisplayName : app.ProcessName);
        if (key.Length == 0)
            return;

        if (!apps.TryGetValue(key, out var existing))
        {
            apps[key] = app.Clone();
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.ExecutablePath) && !string.IsNullOrWhiteSpace(app.ExecutablePath))
            existing.ExecutablePath = app.ExecutablePath;
        if (existing.DisplayName.Length == 0 && app.DisplayName.Length > 0)
            existing.DisplayName = app.DisplayName;

        foreach (var alias in app.Aliases.Where(alias => !existing.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)))
            existing.Aliases.Add(alias);
    }

    private static VoiceCommandApp ApplyAliases(
        VoiceCommandApp app,
        IReadOnlyDictionary<string, string[]>? aliases)
    {
        var copy = app.Clone();
        if (aliases is null)
            return copy;

        var key = copy.ProcessName.Trim().TrimEnd('.');
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            key = key[..^4];

        if (!aliases.TryGetValue(key, out var configuredAliases))
            return copy;

        foreach (var alias in configuredAliases.Where(alias => !copy.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)))
            copy.Aliases.Add(alias);
        return copy;
    }

    private static IEnumerable<VoiceCommandApp> GetAliasApplications(
        IReadOnlyDictionary<string, string[]>? aliases)
    {
        if (aliases is null)
            yield break;

        foreach (var (processName, names) in aliases)
        {
            yield return new VoiceCommandApp
            {
                DisplayName = processName,
                ProcessName = processName,
                Aliases = names.ToList(),
            };
        }
    }

    private static int GetMatchScore(string spokenName, VoiceCommandApp app)
    {
        if (spokenName.Length == 0)
            return 0;

        var values = new[] { app.DisplayName, app.ProcessName }.Concat(app.Aliases);
        var best = 0;
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0)
                continue;

            if (normalized.Contains(spokenName, StringComparison.Ordinal)
                || spokenName.Contains(normalized, StringComparison.Ordinal))
            {
                best = Math.Max(best, 100);
                continue;
            }

            var distance = LevenshteinDistance(spokenName, normalized);
            var allowed = Math.Max(1, Math.Min(3, normalized.Length / 4));
            if (distance <= allowed)
                best = Math.Max(best, 80 - distance * 10);
        }

        return best;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1), substitution);
            }
            previous = current;
        }

        return previous[right.Length];
    }

    private static string ExtractExecutablePath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return "";

        var path = displayIcon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 0)
            path = path[..comma].Trim().Trim('"');

        return File.Exists(path) ? path : "";
    }

    private static string TryResolveShortcut(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return "";

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            var target = Convert.ToString(shortcut.TargetPath) ?? "";
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
            return File.Exists(target) ? target : "";
        }
        catch
        {
            return "";
        }
    }
}