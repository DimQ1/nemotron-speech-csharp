namespace VoiceType.WinUI.Services;

/// <summary>
/// Prevents duplicate child windows across ALL app instances (packaged MSIX and unpackaged).
/// Static OpenInstance fields only guard within a single process — a named mutex is
/// visible machine-wide, so a Settings/Mixer/Downloader window opened from the installed
/// Store package also blocks the same window opened from a debug build (and vice versa).
/// </summary>
public static class ChildWindowGuard
{
    private static readonly Dictionary<string, Mutex> _heldMutexes = new();

    /// <summary>
    /// Attempts to acquire the global guard for a child window type.
    /// Returns true when this process may open the window; false when another
    /// process already has it open. On success the mutex stays held until
    /// <see cref="Release"/> is called (from the window's Closed handler).
    /// </summary>
    public static bool TryAcquire(string windowKey)
    {
        var mutex = new Mutex(initiallyOwned: false, name: $@"Global\VoiceType.ChildWindow.{windowKey}");
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed without releasing — we now own it, proceed.
        }

        lock (_heldMutexes)
            _heldMutexes[windowKey] = mutex;
        return true;
    }

    /// <summary>Releases the guard so other processes can open the window again.</summary>
    public static void Release(string windowKey)
    {
        lock (_heldMutexes)
        {
            if (_heldMutexes.Remove(windowKey, out var mutex))
            {
                try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
                mutex.Dispose();
            }
        }
    }
}
