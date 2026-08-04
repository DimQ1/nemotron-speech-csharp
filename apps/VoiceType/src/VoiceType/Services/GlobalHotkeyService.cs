using System.Runtime.InteropServices;
using System.Windows.Input;

namespace VoiceType.Services;

/// <summary>
/// Registers a global system-wide hotkey via <c>RegisterHotKey</c> WinAPI.
/// The owning window receives <c>WM_HOTKEY</c> (0x0312) messages.
/// </summary>
public static class GlobalHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;
    private const int MOD_NOREPEAT = 0x4000;

    private static int _nextId = 1;
    private static nint _hwnd;
    private static readonly List<int> _registeredIds = new();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    /// <summary>Message code for WM_HOTKEY.</summary>
    public static int WmHotkey => WM_HOTKEY;

    /// <summary>Whether any hotkey is currently registered.</summary>
    public static bool IsRegistered => _registeredIds.Count > 0;

    /// <summary>
    /// Parse a hotkey string (e.g. "Ctrl+Shift+V") and register it.
    /// Returns the hotkey ID (0 = failure) which is passed as wParam in WM_HOTKEY.
    /// </summary>
    public static int Register(nint hwnd, string hotkeyString)
    {
        _hwnd = hwnd;

        if (!TryParse(hotkeyString, out uint mods, out uint vk))
            return 0;

        int id = _nextId++;
        if (RegisterHotKey(hwnd, id, mods, vk))
        {
            _registeredIds.Add(id);
            return id;
        }
        return 0;
    }

    /// <summary>Unregister a specific hotkey by ID.</summary>
    public static void Unregister(int id)
    {
        if (_hwnd != nint.Zero)
            UnregisterHotKey(_hwnd, id);
        _registeredIds.Remove(id);
    }

    /// <summary>Unregister all hotkeys.</summary>
    public static void UnregisterAll()
    {
        foreach (var id in _registeredIds.ToArray())
        {
            if (_hwnd != nint.Zero)
                UnregisterHotKey(_hwnd, id);
        }
        _registeredIds.Clear();
    }

    /// <summary>
    /// Parse "Ctrl+Shift+V" → modifiers + virtual key code.
    /// </summary>
    public static bool TryParse(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries);
        string? keyPart = null;

        foreach (var part in parts)
        {
            var upper = part.ToUpperInvariant();
            switch (upper)
            {
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "ALT": modifiers |= MOD_ALT; break;
                case "WIN" or "WINDOWS": modifiers |= MOD_WIN; break;
                default: keyPart = part; break;
            }
        }

        if (keyPart is null) return false;

        // Try to convert the key string to a Key enum, then to VK
        var key = keyPart.Length == 1
            ? KeyInterop.KeyFromVirtualKey(System.Text.Encoding.ASCII.GetBytes(keyPart.ToUpper())[0])
            : Enum.TryParse<Key>(keyPart, ignoreCase: true, out var parsed)
                ? parsed
                : Key.None;

        if (key == Key.None && keyPart.Length == 1)
            key = (Key)System.Text.Encoding.ASCII.GetBytes(keyPart.ToUpper())[0];

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        // Prevent hotkey from auto-repeating
        modifiers |= MOD_NOREPEAT;

        return vk != 0 && modifiers != MOD_NOREPEAT;
    }
}
