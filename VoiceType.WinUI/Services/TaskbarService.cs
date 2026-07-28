using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Controls Windows taskbar button state: overlay icon and progress bar.
/// Used to show "typing in progress" indicator when text injection is active.
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private nint _hwnd;
    private ITaskbarList3? _taskbarList;
    private nint _overlayIcon;
    private bool _isIndicating;
    private bool _disposed;

    /// <summary>Initialize with the main window handle. Call once after window creation.</summary>
    public void Initialize(nint hwnd)
    {
        _hwnd = hwnd;
        try
        {
            var hr = CoCreateInstance(
                typeof(TaskbarList).GUID,
                nint.Zero,
                CLSCTX_ALL,
                typeof(ITaskbarList3).GUID,
                out var obj);
            if (hr == 0 && obj is not null)
            {
                _taskbarList = (ITaskbarList3)obj;
                _taskbarList.HrInit();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarService init failed: {ex.Message}");
        }
    }

    /// <summary>Show "typing" indicator: overlay icon + indeterminate progress.</summary>
    public void StartTypingIndicator()
    {
        if (_taskbarList is null || _hwnd == nint.Zero || _isIndicating) return;
        _isIndicating = true;

        try
        {
            // Create a small overlay icon (16x16) with a pencil/typing glyph
            _overlayIcon = CreateTypingOverlayIcon();
            if (_overlayIcon != nint.Zero)
            {
                _taskbarList.SetOverlayIcon(_hwnd, _overlayIcon, "Typing...");
            }

            // Indeterminate progress bar (marquee) — shows activity without known progress
            _taskbarList.SetProgressState(_hwnd, TBPF_INDETERMINATE);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarService StartTypingIndicator failed: {ex.Message}");
        }
    }

    /// <summary>Hide typing indicator.</summary>
    public void StopTypingIndicator()
    {
        if (_taskbarList is null || _hwnd == nint.Zero || !_isIndicating) return;
        _isIndicating = false;

        try
        {
            _taskbarList.SetOverlayIcon(_hwnd, nint.Zero, null);
            _taskbarList.SetProgressState(_hwnd, TBPF_NOPROGRESS);

            if (_overlayIcon != nint.Zero)
            {
                DestroyIcon(_overlayIcon);
                _overlayIcon = nint.Zero;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarService StopTypingIndicator failed: {ex.Message}");
        }
    }

    /// <summary>Clean up resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        StopTypingIndicator();
        if (_overlayIcon != nint.Zero)
        {
            DestroyIcon(_overlayIcon);
            _overlayIcon = nint.Zero;
        }
        if (_taskbarList is not null)
        {
            Marshal.ReleaseComObject(_taskbarList);
            _taskbarList = null;
        }
        _disposed = true;
    }

    // ---- Icon creation (16x16, green dot) using raw Win32 ----

    private static nint CreateTypingOverlayIcon()
    {
        // Create a simple 16x16 icon using raw Win32 GDI — no System.Drawing dependency.
        // The icon is a green circle on transparent background.

        const int size = 16;
        var hdcScreen = GetDC(nint.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hdcMask = CreateCompatibleDC(hdcScreen);

        // Color bitmap (32bpp with alpha)
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
                biSizeImage = size * size * 4
            }
        };

        var hbmColor = CreateDIBSection(hdcMem, ref bmi, 0, out var bits, nint.Zero, 0);
        var hbmMask = CreateBitmap(size, size, 1, 1, nint.Zero);

        // Fill color bitmap: green circle (RGBA, premultiplied not needed for overlay)
        var pixelData = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var idx = (y * size + x) * 4;
                var dx = x - size / 2 + 0.5;
                var dy = y - size / 2 + 0.5;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < size / 2 - 1)
                {
                    // Green: B=0, G=175, R=76, A=255
                    pixelData[idx + 0] = 0;      // B
                    pixelData[idx + 1] = 175;    // G
                    pixelData[idx + 2] = 76;     // R
                    pixelData[idx + 3] = 255;    // A
                }
                else
                {
                    pixelData[idx + 3] = 0;      // A = transparent
                }
            }
        }
        Marshal.Copy(pixelData, 0, bits, pixelData.Length);

        // Mask: 0 = opaque, 1 = transparent
        var hbmMaskOld = SelectObject(hdcMask, hbmMask);
        var brush = GetStockObject(BLACK_BRUSH);
        SelectObject(hdcMask, brush);
        Rectangle(hdcMask, 0, 0, size, size);

        // Circle in mask = 0 (opaque)
        var whiteBrush = GetStockObject(WHITE_BRUSH);
        SelectObject(hdcMask, whiteBrush);
        Ellipse(hdcMask, 1, 1, size - 1, size - 1);

        SelectObject(hdcMask, hbmMaskOld);

        var ii = new ICONINFO
        {
            fIcon = true,
            hbmColor = hbmColor,
            hbmMask = hbmMask
        };

        var hIcon = CreateIconIndirect(ref ii);

        // Cleanup
        DeleteObject(hbmColor);
        DeleteObject(hbmMask);
        DeleteDC(hdcMem);
        DeleteDC(hdcMask);
        ReleaseDC(nint.Zero, hdcScreen);

        return hIcon;
    }

    // ---- Win32 / COM interop ----

    private const uint CLSCTX_ALL = 0x17;
    private const int TBPF_NOPROGRESS = 0x0;
    private const int TBPF_INDETERMINATE = 0x1;
    private const int TBPF_NORMAL = 0x2;
    private const int TBPF_ERROR = 0x4;
    private const int TBPF_PAUSED = 0x8;
    private const int BLACK_BRUSH = 4;
    private const int WHITE_BRUSH = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int i);

    [DllImport("gdi32.dll")]
    private static extern bool Rectangle(nint hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(nint hdc, int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO ii);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(nint hwnd, int tbpFlags);
        void RegisterTab(nint hwndTab, nint hwndMDI);
        void UnregisterTab(nint hwndTab);
        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        void SetTabActive(nint hwndTab, nint hwndMDI, uint dwReserved);
        void ThumbBarAddButtons(nint hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        void ThumbBarUpdateButtons(nint hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        void ThumbBarSetImageList(nint hwnd, nint himl);
        void SetOverlayIcon(nint hwnd, nint hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
        void SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
        void SetThumbnailClip(nint hwnd, nint prcClip);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }
}
