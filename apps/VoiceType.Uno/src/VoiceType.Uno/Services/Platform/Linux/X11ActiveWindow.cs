using System.Runtime.InteropServices;

namespace VoiceType.Uno.Services.Platform.Linux;

/// <summary>
/// Tells whether the focused X11 window belongs to this process, through the window manager's
/// <c>_NET_ACTIVE_WINDOW</c> and the window's <c>_NET_WM_PID</c>. Used so dictated text is not typed into
/// VoiceType itself, where a typed space or Enter would press the focused button.
/// </summary>
internal static class X11ActiveWindow
{
    private const nuint XA_CARDINAL = 6;
    private const nuint XA_WINDOW = 33;

    /// <summary>
    /// True/false when the answer is known; null when it is not (no X display, no window manager hint, a native
    /// Wayland window has the focus, or the window has no PID).
    /// </summary>
    public static bool? IsOwnedByThisProcess()
    {
        if (!LinuxSession.HasX11)
            return null;

        try
        {
            var display = XOpenDisplay(nint.Zero);
            if (display == nint.Zero)
                return null;

            try
            {
                var active = ReadProperty(display, XDefaultRootWindow(display), "_NET_ACTIVE_WINDOW", XA_WINDOW);
                if (active == 0)
                    return null;

                var pid = ReadProperty(display, active, "_NET_WM_PID", XA_CARDINAL);
                return pid == 0 ? null : pid == (nuint)Environment.ProcessId;
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            // libX11 missing or an unexpected reply: unknown.
            return null;
        }
    }

    /// <summary>The first 32-bit value of a property, or 0 when it is absent or of another type.</summary>
    private static nuint ReadProperty(nint display, nuint window, string name, nuint type)
    {
        var atom = XInternAtom(display, name, true);
        if (atom == 0)
            return 0;

        var status = XGetWindowProperty(display, window, atom, 0, 1, false, type,
            out var actualType, out var actualFormat, out var count, out _, out var data);
        if (status != 0 || data == nint.Zero)
            return 0;

        try
        {
            // 32-bit properties come back as C longs (8 bytes on x86-64).
            return actualType == type && actualFormat == 32 && count >= 1
                ? (nuint)Marshal.ReadIntPtr(data)
                : 0;
        }
        finally
        {
            XFree(data);
        }
    }

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint XOpenDisplay(nint displayName);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XGetWindowProperty(
        nint display, nuint window, nuint property, nint longOffset, nint longLength,
        [MarshalAs(UnmanagedType.Bool)] bool delete, nuint requestedType,
        out nuint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out nint data);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XFree(nint data);
}
