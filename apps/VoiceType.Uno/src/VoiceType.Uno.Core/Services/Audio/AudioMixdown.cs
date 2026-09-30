namespace VoiceType.Uno.Services.Audio;

/// <summary>
/// Sample-level mixing used by the PulseAudio "Mix" capture mode (microphone +
/// monitor in the same stream). Pure math, so the level policy is testable without
/// touching native audio.
/// </summary>
public static class AudioMixdown
{
    /// <summary>
    /// Averages two equally sized capture batches at half gain and clamps to the
    /// valid sample range. A missing side contributes silence, so a batch that only
    /// one device produced still reaches the recognizer.
    /// </summary>
    /// <returns>The mixed batch, or <c>null</c> when neither side produced samples.</returns>
    public static float[]? Average(float[]? microphone, float[]? loopback)
    {
        var count = Math.Max(microphone?.Length ?? 0, loopback?.Length ?? 0);
        if (count == 0)
            return null;

        var mixed = new float[count];
        for (var i = 0; i < count; i++)
        {
            var microphoneSample = microphone is not null && i < microphone.Length ? microphone[i] : 0f;
            var loopbackSample = loopback is not null && i < loopback.Length ? loopback[i] : 0f;
            mixed[i] = Math.Clamp((microphoneSample + loopbackSample) * 0.5f, -1f, 1f);
        }

        return mixed;
    }
}
