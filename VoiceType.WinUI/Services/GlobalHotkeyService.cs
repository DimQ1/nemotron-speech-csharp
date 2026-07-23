using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Registers a global system-wide hotkey via <c>RegisterHotKey</c> WinAPI.
/// The owning window receives <c>WM_HOTKEY</c> (0x0312) messages.
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;
    private const int MOD_NOREPEAT = 0x4000;

    private int _nextId = 1;
    private nint _hwnd;
    private readonly List<int> _registeredIds = new();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public int WmHotkey => WM_HOTKEY;
    public bool IsRegistered => _registeredIds.Count > 0;

    public int Register(nint hwnd, string hotkeyString)
    {
        _hwnd = hwnd;

        if (!TryParse(hotkeyString, out uint mods, out uint vk))
        {
            Console.WriteLine($"[VoiceType] Hotkey parse failed: {hotkeyString}");
            return 0;
        }

        int id = _nextId++;
        if (RegisterHotKey(hwnd, id, mods, vk))
        {
            _registeredIds.Add(id);
            Console.WriteLine($"[VoiceType] Hotkey registered: {hotkeyString} -> id={id}, vk=0x{vk:X}, mods=0x{mods:X}");
            return id;
        }

        var err = Marshal.GetLastWin32Error();
        Console.WriteLine($"[VoiceType] Hotkey registration failed: {hotkeyString}, error={err}");
        return 0;
    }

    public void Unregister(int id)
    {
        if (_hwnd != nint.Zero)
            UnregisterHotKey(_hwnd, id);
        _registeredIds.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registeredIds.ToArray())
        {
            if (_hwnd != nint.Zero)
                UnregisterHotKey(_hwnd, id);
        }
        _registeredIds.Clear();
    }

    public bool TryParse(string hotkey, out uint modifiers, out uint vk)
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

        vk = ParseKeyToVk(keyPart);
        modifiers |= MOD_NOREPEAT;

        return vk != 0 && modifiers != MOD_NOREPEAT;
    }

    private static uint ParseKeyToVk(string keyPart)
    {
        if (keyPart.Length == 1)
        {
            char c = char.ToUpperInvariant(keyPart[0]);
            if (c is >= 'A' and <= 'Z') return (uint)c;
            if (c is >= '0' and <= '9') return (uint)c;
        }

        return keyPart.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "OEM_3" or "OEM3" or "GRAVE" or "TILDE" => 0xC0,
            "OEM_MINUS" or "MINUS" => 0xBD,
            "OEM_PLUS" or "PLUS" or "EQUALS" => 0xBB,
            "OEM_COMMA" or "COMMA" => 0xBC,
            "OEM_PERIOD" or "PERIOD" => 0xBE,
            _ => 0
        };
    }
}
