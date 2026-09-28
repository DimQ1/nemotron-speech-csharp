namespace SpeechLib.Audio;

/// <summary>
/// Sums two mono capture channels with per-channel gain into one batch.
/// Channels of unequal length are treated as silence past their end (a source that is
/// idle this drain — e.g. loopback with nothing playing — must not stall the other).
/// The sum is hard-limited to the nominal ±1 range so a loud mix cannot wrap or
/// overdrive the recognizer's feature extraction.
/// </summary>
public static class CaptureMixer
{
    /// <summary>Number of samples <see cref="Mix"/> writes for the given channel lengths.</summary>
    public static int OutputLength(int aLength, int bLength) => Math.Max(aLength, bLength);

    /// <summary>
    /// Mix <paramref name="a"/> × <paramref name="gainA"/> + <paramref name="b"/> × <paramref name="gainB"/>
    /// into <paramref name="output"/>, clamped to [−1, 1]. Returns the number of samples written.
    /// </summary>
    public static int Mix(
        ReadOnlySpan<float> a, float gainA,
        ReadOnlySpan<float> b, float gainB,
        Span<float> output)
    {
        var count = OutputLength(a.Length, b.Length);
        if (output.Length < count)
            throw new ArgumentException("Output buffer is too small for the mixed batch.", nameof(output));

        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
            output[i] = Clamp(a[i] * gainA + b[i] * gainB);

        for (var i = shared; i < a.Length; i++)
            output[i] = Clamp(a[i] * gainA);

        for (var i = shared; i < b.Length; i++)
            output[i] = Clamp(b[i] * gainB);

        return count;
    }

    private static float Clamp(float value) => value > 1f ? 1f : value < -1f ? -1f : value;
}
