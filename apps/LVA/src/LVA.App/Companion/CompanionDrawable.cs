using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace LVA.App.Companion;

/// <summary>
/// SkiaSharp-drawn companion ("blob-v1" skin): body, eyes with pupils that
/// track the pointer, ears, mouth; state-driven animation (breathing,
/// listening pulse, hops, squash). Mirrors the prototype's geometry.
/// </summary>
public sealed class CompanionDrawable : IDrawable
{
    private readonly CompanionStateMachine _stateMachine;

    /// <summary>Pointer position relative to the view center, normalized −1..1.</summary>
    public PointF PointerOffset { get; set; }

    /// <summary>Microphone level 0..1 for the listening pulse (TZ §14.3).</summary>
    public float AudioLevel { get; set; }

    /// <summary>Animation clock (seconds, monotonic).</summary>
    public double Time { get; set; }

    public CompanionDrawable(CompanionStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();
        var cx = dirtyRect.Center.X;
        var cy = dirtyRect.Center.Y;
        var size = Math.Min(dirtyRect.Width, dirtyRect.Height) * 0.72f;

        var state = _stateMachine.Current;
        var t = (float)Time;

        // ── State-driven transforms (breathe / pulse / hop / squash / dangle)
        float scaleX = 1f, scaleY = 1f, translateY = 0f, rotate = 0f;
        switch (state)
        {
            case CompanionState.Sleeping:
                var b = Breathe(t, 4.5f);
                scaleX = b.X; scaleY = b.Y;
                break;
            case CompanionState.Idle:
            case CompanionState.Greeting:
                var b2 = Breathe(t, 3f);
                scaleX = b2.X; scaleY = b2.Y;
                if (state == CompanionState.Greeting && t % 1.6f < 0.8f)
                    translateY = Hop(t, 0.5f);
                break;
            case CompanionState.Listening:
                // Pulse in sync with the voice level (1–1.5 Hz).
                var amp = 0.05f + AudioLevel * 0.08f;
                var phase = MathF.Sin(t * MathF.Tau * 1.2f);
                scaleX = 1 + amp * phase;
                scaleY = 1 - amp * phase;
                break;
            case CompanionState.Recognizing:
                rotate = MathF.Sin(t * MathF.Tau / 2f) * 5f;
                break;
            case CompanionState.Activated:
            case CompanionState.Success:
                translateY = Hop(t % 0.6f, 0.6f);
                break;
            case CompanionState.Working:
                rotate = MathF.Sin(t * MathF.Tau * 0.8f) * 4f;
                translateY = MathF.Abs(MathF.Sin(t * MathF.Tau * 0.8f)) * size * 0.03f;
                break;
            case CompanionState.Petting:
                rotate = MathF.Sin(t * MathF.Tau * 1.1f) * 1.5f;
                scaleX = 1 + 0.02f * MathF.Sin(t * MathF.Tau * 1.1f);
                break;
            case CompanionState.Poked:
                var sq = Squash(t % 0.45f, 0.45f);
                scaleX = sq.X; scaleY = sq.Y;
                break;
            case CompanionState.Carried:
                rotate = MathF.Sin(t * MathF.Tau * 0.5f) * 7f;
                break;
            case CompanionState.Curious:
                rotate = Math.Clamp(PointerOffset.X * 8f, -8f, 8f);
                break;
            case CompanionState.Speaking:
                var b3 = Breathe(t, 2.2f);
                scaleX = b3.X; scaleY = b3.Y;
                break;
        }

        canvas.Translate(cx, cy + translateY);
        canvas.Rotate(rotate);
        canvas.Scale(size / 100f * scaleX, size / 100f * scaleY);

        DrawBody(canvas, state);
        DrawEars(canvas, state, t);
        DrawEyes(canvas, state);
        DrawMouth(canvas, state, t);

        canvas.RestoreState();
    }

    private static (float X, float Y) Breathe(float t, float period)
    {
        var p = MathF.Sin(t * MathF.Tau / period);
        return (1 + 0.045f * p, 1 - 0.04f * p);
    }

    private static float Hop(float t, float period)
    {
        var p = t / period;
        return p < 0.4f ? -22f * MathF.Sin(p / 0.4f * MathF.PI) : 0f;
    }

    private static (float X, float Y) Squash(float t, float period)
    {
        var p = t / period;
        if (p < 0.35f) return (1.25f, 0.6f);
        if (p < 0.7f) return (0.9f, 1.12f);
        return (1f, 1f);
    }

    private void DrawBody(ICanvas canvas, CompanionState state)
    {
        var baseColor = state switch
        {
            CompanionState.Success => UiTokens.Success,
            CompanionState.Error => UiTokens.Error,
            CompanionState.Sleeping => Dim(Color.FromArgb("#6A5AE0"), 0.75f),
            _ => Color.FromArgb("#6A5AE0")
        };

        // The prototype is a soft, slightly asymmetric bean rather than a circle.
        using var path = new PathF();
        path.MoveTo(-2, -50);
        path.CurveTo(27, -53, 51, -29, 49, 4);
        path.CurveTo(47, 34, 24, 51, -5, 49);
        path.CurveTo(-33, 48, -51, 26, -49, -5);
        path.CurveTo(-47, -32, -28, -48, -2, -50);
        path.Close();

        if (state is CompanionState.Success or CompanionState.Error)
        {
            canvas.FillColor = baseColor;
            canvas.FillPath(path);
        }
        else
        {
            // Gradient 145deg from #8d7dff to #6a5ae0 (mockup).
            var light = Color.FromArgb("#8D7DFF");
            var dark = Color.FromArgb("#6A5AE0");
            canvas.FillColor = baseColor;
            canvas.FillPath(path);

            canvas.SaveState();
            canvas.ClipPath(path);
            // Top-left light wash.
            canvas.FillColor = Color.FromRgba(light.Red, light.Green, light.Blue, 0.55f);
            canvas.FillEllipse(-46, -48, 62, 78);
            // Bottom-right inner shadow (inset -8px -10px).
            canvas.FillColor = Color.FromRgba(dark.Red * 0.4f, dark.Green * 0.4f, dark.Blue * 0.5f, 0.5f);
            canvas.FillEllipse(-6, -2, 56, 52);
            // Specular highlight.
            canvas.FillColor = Color.FromRgba(255f, 255f, 255f, 0.16f);
            canvas.FillEllipse(-30, -40, 26, 17);
            canvas.RestoreState();
        }
    }

    private void DrawEars(ICanvas canvas, CompanionState state, float t)
    {
        canvas.FillColor = state switch
        {
            CompanionState.Success => UiTokens.Success,
            CompanionState.Error => UiTokens.Error,
            _ => Color.FromArgb("#7566EA")
        };
        var (lRot, rRot) = state switch
        {
            CompanionState.Listening or CompanionState.Activated => (-30f, 30f),
            CompanionState.Error => (20f, -20f),
            CompanionState.Carried => (-60f, 60f),
            CompanionState.Curious or CompanionState.Petting => (-26f, 26f),
            _ => (-14f, 14f)
        };
        var lLift = state is CompanionState.Listening or CompanionState.Activated ? -8f : 0f;

        canvas.SaveState();
        canvas.Rotate(lRot, -20f, -44f);
        canvas.FillEllipse(-32f, -56f + lLift, 20, 26);
        canvas.FillColor = Color.FromRgba(177, 166, 255, 90);
        canvas.FillEllipse(-29f, -54f + lLift, 8, 15);
        canvas.RestoreState();

        canvas.FillColor = state switch
        {
            CompanionState.Success => UiTokens.Success,
            CompanionState.Error => UiTokens.Error,
            _ => Color.FromArgb("#7566EA")
        };
        canvas.SaveState();
        canvas.Rotate(rRot, 20f, -44f);
        canvas.FillEllipse(12f, -56f + lLift, 20, 26);
        canvas.FillColor = Color.FromRgba(177, 166, 255, 90);
        canvas.FillEllipse(15f, -54f + lLift, 8, 15);
        canvas.RestoreState();
    }

    private void DrawEyes(ICanvas canvas, CompanionState state)
    {
        if (state == CompanionState.Sleeping)
        {
            // Closed eyes — thin lines.
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 2.5f;
            canvas.DrawLine(-25f, -10f, -15f, -10f);
            canvas.DrawLine(15f, -10f, 25f, -10f);
            return;
        }

        var eyeScaleY = state == CompanionState.Petting ? 0.35f : 1f;
        foreach (var ex in new[] { -20f, 20f })
        {
            canvas.FillColor = Colors.White;
            canvas.SaveState();
            canvas.Translate(ex, -12f);
            canvas.Scale(1f, eyeScaleY);
            canvas.FillEllipse(-6.5f, -7.5f, 13f, 15f);
            canvas.RestoreState();

            // Pupil tracks the pointer (limited range).
            var px = ex + Math.Clamp(PointerOffset.X * 4.5f, -4.5f, 4.5f);
            var py = -12f + Math.Clamp(PointerOffset.Y * 4.5f, -4.5f, 4.5f);
            canvas.FillColor = UiTokens.Bg;
            canvas.SaveState();
            canvas.Translate(px, py);
            canvas.Scale(1f, eyeScaleY);
            canvas.FillEllipse(-3f, -3.5f, 6f, 7f);
            canvas.RestoreState();
        }
    }

    private void DrawMouth(ICanvas canvas, CompanionState state, float t)
    {
        canvas.StrokeColor = Colors.White.WithAlpha(0.9f);
        canvas.StrokeSize = 3f;

        switch (state)
        {
            case CompanionState.Error:
                // Inverted smile.
                canvas.DrawArc(-12f, 18f, 24, 10, 200, 140, false, false);
                break;
            case CompanionState.Speaking:
                // Animated open mouth in sync with the speech level.
                var open = 2.5f + 5f * MathF.Abs(MathF.Sin(t * MathF.Tau * 1.8f));
                canvas.FillColor = Colors.White.WithAlpha(0.9f);
                canvas.FillEllipse(-9f, 12f, 18, open * 2);
                break;
            default:
                canvas.DrawArc(-12f, 8f, 24, 12, 20, 140, false, false);
                break;
        }
    }

    private static Color Dim(Color c, float factor)
        => Color.FromRgba(c.Red * factor, c.Green * factor, c.Blue * factor, c.Alpha);
}
