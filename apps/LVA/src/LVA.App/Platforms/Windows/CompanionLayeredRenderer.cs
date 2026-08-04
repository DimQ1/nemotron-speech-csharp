using System.Runtime.InteropServices;
using LVA.App.Companion;
using SkiaSharp;
using MColor = Microsoft.Maui.Graphics.Color;

namespace LVA.App.WinUI;

/// <summary>
/// Renders the companion pet into an offscreen SkiaSharp bitmap each frame and
/// pushes it to the layered HWND via <c>UpdateLayeredWindow</c> (per-pixel
/// alpha). This is the only approach that yields true transparency against the
/// WinUI DirectComposition swap chain. The window's MAUI content is hidden;
/// this renderer owns the visible output.
/// </summary>
internal sealed class CompanionLayeredRenderer : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly CompanionStateMachine _stateMachine;
    private readonly Thread _thread;
    private volatile bool _running;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private static readonly MColor Accent = MColor.FromArgb("#6A5AE0");
    private static readonly MColor AccentLight = MColor.FromArgb("#8D7DFF");
    private static readonly MColor Success = MColor.FromArgb("#3ECF8E");
    private static readonly MColor Error = MColor.FromArgb("#FF6B6B");

    public CompanionLayeredRenderer(IntPtr hwnd, CompanionStateMachine stateMachine)
    {
        _hwnd = hwnd;
        _stateMachine = stateMachine;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "CompanionRenderer" };
    }

    public void Start() { _running = true; _thread.Start(); }
    public void Dispose() => _running = false;

    private void RenderLoop()
    {
        GetWindowRect(_hwnd, out var rect);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) { w = 270; h = 330; }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmi = MakeBitmapInfo(w, h);
        var dib = CreateDIBSection(memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        SelectObject(memDc, dib);

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap();
        bitmap.InstallPixels(info, bits);
        using var canvas = new SKCanvas(bitmap);

        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        var srcPt = new POINT { X = 0, Y = 0 };
        var sz = new SIZE { cx = w, cy = h };

        using var paint = new SKPaint { IsAntialias = true };

        while (_running)
        {
            var t = (float)(DateTime.UtcNow - _startedAt).TotalSeconds;
            canvas.Clear(SKColors.Transparent);
            DrawPet(canvas, paint, _stateMachine.Current, t, w, h);
            canvas.Flush();

            GetWindowRect(_hwnd, out var cur);
            var winPt = new POINT { X = cur.Left, Y = cur.Top };
            UpdateLayeredWindow(_hwnd, screenDc, ref winPt, ref sz, memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
            Thread.Sleep(33);
        }

        DeleteObject(dib);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static void DrawPet(SKCanvas canvas, SKPaint paint, CompanionState state, float t, int w, int h)
    {
        var cx = w / 2f;
        var cy = h / 2f + h * 0.05f;
        var size = Math.Min(w, h) * 0.62f;

        float sx = 1f, sy = 1f, ty = 0f, rot = 0f;
        switch (state)
        {
            case CompanionState.Sleeping: var b = Breathe(t, 4.5f); sx = b.X; sy = b.Y; break;
            case CompanionState.Idle: var b2 = Breathe(t, 3f); sx = b2.X; sy = b2.Y; break;
            case CompanionState.Listening:
                var amp = 0.06f;
                var ph = MathF.Sin(t * MathF.Tau * 1.2f);
                sx = 1 + amp * ph; sy = 1 - amp * ph; break;
            case CompanionState.Activated:
            case CompanionState.Success: ty = Hop(t % 0.6f, 0.6f, size); break;
        }

        canvas.Save();
        canvas.Translate(cx, cy + ty);
        canvas.RotateDegrees(rot);
        canvas.Scale(size / 100f * sx, size / 100f * sy);

        var bodyColor = state switch
        {
            CompanionState.Success => Success,
            CompanionState.Error => Error,
            CompanionState.Sleeping => Dim(Accent, 0.75f),
            _ => Accent
        };

        using var bodyPath = new SKPath();
        bodyPath.MoveTo(-2, -50);
        bodyPath.CubicTo(27, -53, 51, -29, 49, 4);
        bodyPath.CubicTo(47, 34, 24, 51, -5, 49);
        bodyPath.CubicTo(-33, 48, -51, 26, -49, -5);
        bodyPath.CubicTo(-47, -32, -28, -48, -2, -50);
        bodyPath.Close();

        paint.Style = SKPaintStyle.Fill;
        if (state is CompanionState.Success or CompanionState.Error)
        {
            paint.Color = ToSk(bodyColor);
            canvas.DrawPath(bodyPath, paint);
        }
        else
        {
            paint.Color = ToSk(Accent);
            canvas.DrawPath(bodyPath, paint);
            canvas.Save();
            canvas.ClipPath(bodyPath);
            paint.Color = WithAlpha(AccentLight, 0.55f);
            canvas.DrawOval(new SKRect(-46, -48, 16, 30), paint);
            paint.Color = WithAlpha(Dim(Accent, 0.45f), 0.5f);
            canvas.DrawOval(new SKRect(-6, -2, 50, 50), paint);
            paint.Color = WithAlpha(MColor.FromArgb("#FFFFFF"), 0.16f);
            canvas.DrawOval(new SKRect(-30, -40, -4, -23), paint);
            canvas.Restore();
        }

        // Ears
        paint.Color = ToSk(state is CompanionState.Success ? Success : state is CompanionState.Error ? Error : MColor.FromArgb("#7566EA"));
        var (lRot, rRot) = state switch
        {
            CompanionState.Listening or CompanionState.Activated => (-30f, 30f),
            CompanionState.Error => (20f, -20f),
            _ => (-14f, 14f)
        };
        canvas.Save();
        canvas.RotateDegrees(lRot, -20, -44);
        canvas.DrawOval(new SKRect(-32, -56, -12, -30), paint);
        canvas.Restore();
        canvas.Save();
        canvas.RotateDegrees(rRot, 20, -44);
        canvas.DrawOval(new SKRect(12, -56, 32, -30), paint);
        canvas.Restore();

        // Eyes
        if (state == CompanionState.Sleeping)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 2.5f;
            paint.Color = SKColors.White;
            canvas.DrawLine(-25, -10, -15, -10, paint);
            canvas.DrawLine(15, -10, 25, -10, paint);
            paint.Style = SKPaintStyle.Fill;
        }
        else
        {
            foreach (var ex in new[] { -20f, 20f })
            {
                paint.Color = SKColors.White;
                canvas.DrawOval(new SKRect(ex - 6.5f, -19.5f, ex + 6.5f, -4.5f), paint);
                paint.Color = ToSk(MColor.FromArgb("#1B1E26"));
                canvas.DrawOval(new SKRect(ex - 3f, -15.5f, ex + 3f, -8.5f), paint);
            }
        }

        // Mouth
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 3f;
        paint.Color = WithAlpha(MColor.FromArgb("#FFFFFF"), 0.9f);
        if (state == CompanionState.Error)
        {
            using var arc = new SKPath();
            arc.AddArc(new SKRect(-12, 18, 12, 28), 200, 140);
            canvas.DrawPath(arc, paint);
        }
        else
        {
            using var arc = new SKPath();
            arc.AddArc(new SKRect(-12, 8, 12, 20), 20, 140);
            canvas.DrawPath(arc, paint);
        }

        canvas.Restore();
    }

    private static (float X, float Y) Breathe(float t, float period)
    {
        var p = MathF.Sin(t * MathF.Tau / period);
        return (1 + 0.045f * p, 1 - 0.04f * p);
    }

    private static float Hop(float t, float period, float size)
    {
        var p = t / period;
        return p < 0.4f ? -size * 0.12f * MathF.Sin(p / 0.4f * MathF.PI) : 0f;
    }

    private static MColor Dim(MColor c, float f)
        => MColor.FromRgba(c.Red * f, c.Green * f, c.Blue * f, c.Alpha);

    private static SKColor ToSk(MColor c)
        => new((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255), (byte)(c.Alpha * 255));

    private static SKColor WithAlpha(MColor c, float a)
        => new((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255), (byte)(a * 255));

    private static BITMAPINFO MakeBitmapInfo(int w, int h) => new()
    {
        bmiHeader = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        },
        bmiColors = new uint[256]
    };

    private const uint ULW_ALPHA = 0x00000002;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes;
        public ushort biBitCount; public uint biCompression; public uint biSizeImage;
        public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] bmiColors; }
}
