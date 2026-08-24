using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VoiceType.Hotkeys.Windows;

/// <summary>
/// Windows global hotkeys via <c>RegisterHotKey</c> WinAPI. Runs a hidden
/// message-only window on a dedicated background thread so WM_HOTKEY messages
/// are pumped regardless of which window the app currently shows. Register and
/// unregister requests are marshaled to that thread because RegisterHotKey
/// associates the binding with the calling thread's message queue.
/// </summary>
public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP_DRAIN = 0x8001;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const string WindowClassName = "VoiceTypeGlobalHotkeys";

    private readonly WndProcDelegate _wndProc;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _operations = new();

    private nint _hwnd;
    private int _nextId;
    private readonly List<int> _registeredIds = new();
    private int _disposed;

    public event Action<int>? HotkeyPressed;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public WindowsGlobalHotkeyService()
    {
        _wndProc = WndProc;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "VoiceType global hotkeys"
        };
        _thread.Start();
    }

    public Task<int> RegisterAsync(string chord, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !HotkeyChord.TryParse(chord, out var parsed))
            return Task.FromResult(0);

        var vk = KeyToVk(parsed.Key);
        var mods = ToWinMods(parsed);
        if (vk == 0 || mods == 0)
            return Task.FromResult(0);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _operations.Enqueue(() =>
        {
            try
            {
                if (_hwnd == nint.Zero || !RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
                {
                    tcs.TrySetResult(0);
                    return;
                }
                lock (_registeredIds) _registeredIds.Add(id);
                tcs.TrySetResult(id);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        PostDrain();

        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public Task UnregisterAllAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Enqueue(() =>
        {
            try
            {
                UnregisterAllCore();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        PostDrain();

        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _operations.Enqueue(() =>
        {
            try
            {
                UnregisterAllCore();
                PostQuitMessage(0);
            }
            catch
            {
                // Shutting down; never throw from dispose.
            }
        });
        PostDrain();
        return ValueTask.CompletedTask;
    }

    // ── Message loop thread ──────────────────────────────────────────────────

    private void MessageLoop()
    {
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };
        RegisterClassExW(ref wc);

        _hwnd = CreateWindowExW(
            0, WindowClassName, "VoiceTypeHotkeys", 0,
            0, 0, 0, 0,
            new nint(-3),            // HWND_MESSAGE
            nint.Zero, hInstance, nint.Zero);

        if (_hwnd == nint.Zero)
            return;

        // Catch operations enqueued before the window existed.
        DrainOperations();

        while (GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY)
        {
            HotkeyPressed?.Invoke((int)wParam);
            return nint.Zero;
        }

        if (msg == WM_APP_DRAIN)
        {
            DrainOperations();
            return nint.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void DrainOperations()
    {
        while (_operations.TryDequeue(out var op))
            op();
    }

    private void PostDrain()
    {
        var h = _hwnd;
        if (h != nint.Zero)
            PostMessageW(h, WM_APP_DRAIN, nint.Zero, nint.Zero);
    }

    private void UnregisterAllCore()
    {
        lock (_registeredIds)
        {
            foreach (var id in _registeredIds)
            {
                if (_hwnd != nint.Zero)
                    UnregisterHotKey(_hwnd, id);
            }
            _registeredIds.Clear();
        }
    }

    private static uint ToWinMods(HotkeyChord chord)
    {
        uint mods = 0;
        if (chord.Ctrl) mods |= MOD_CONTROL;
        if (chord.Shift) mods |= MOD_SHIFT;
        if (chord.Alt) mods |= MOD_ALT;
        if (chord.Super) mods |= MOD_WIN;
        return mods;
    }

    private static uint KeyToVk(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        return key.ToUpperInvariant() switch
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
            "F13" => 0x7C, "F14" => 0x7D, "F15" => 0x7E, "F16" => 0x7F,
            "F17" => 0x80, "F18" => 0x81, "F19" => 0x82, "F20" => 0x83,
            "F21" => 0x84, "F22" => 0x85, "F23" => 0x86, "F24" => 0x87,
            _ => 0
        };
    }

    // ── Win32 interop ─────────────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}
