namespace SpeechLib.Audio;

/// <summary>
/// Slow automatic gain control for recognizer input. It raises quiet speech toward a
/// target level and never attenuates below unity, so normally leveled audio passes
/// through unchanged.
/// </summary>
/// <remarks>
/// Some models have no input normalization and return nothing at all for quiet
/// microphones: the Nemotron streaming model is silent at a -40 dBFS peak, where
/// Parakeet (per-feature normalization) still transcribes correctly. The gain follows
/// the speech level, measured on 10 ms frames above a noise gate, so pauses do not
/// pump the gain up. A frame whose peak would clip lowers the gain at once.
/// </remarks>
public sealed class AutomaticGainControl
{
    /// <summary>Speech RMS the gain aims for (about -24 dBFS).</summary>
    public const float TargetRms = 0.06f;

    /// <summary>Largest boost applied (+26 dB).</summary>
    public const float MaxGain = 20f;

    /// <summary>Frames quieter than this RMS (about -60 dBFS) are treated as silence.</summary>
    public const float NoiseGateRms = 0.001f;

    private const float PeakCeiling = 0.9f;
    private const float LevelAttack = 0.5f;
    private const float LevelRelease = 0.02f;
    private const float GainRise = 0.05f;

    private readonly int _frameSamples;
    private float _gain = 1f;
    private float _speechLevel;
    private bool _heardSpeech;
    private double _frameSumSquares;
    private float _framePeak;
    private int _frameCount;

    public AutomaticGainControl(int sampleRate = 16000)
    {
        _frameSamples = Math.Max(1, sampleRate / 100);
    }

    /// <summary>Gain currently applied.</summary>
    public float Gain => _gain;

    /// <summary>Returns a new buffer with the gain applied; <paramref name="input"/> is not changed.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new float[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var sample = input[i];
            var boosted = sample * _gain;
            output[i] = boosted > 1f ? 1f : boosted < -1f ? -1f : boosted;

            _frameSumSquares += (double)sample * sample;
            var magnitude = Math.Abs(sample);
            if (magnitude > _framePeak)
                _framePeak = magnitude;

            if (++_frameCount == _frameSamples)
                EndFrame();
        }

        return output;
    }

    private void EndFrame()
    {
        var rms = (float)Math.Sqrt(_frameSumSquares / _frameCount);
        var peak = _framePeak;
        _frameSumSquares = 0;
        _framePeak = 0;
        _frameCount = 0;

        if (rms >= NoiseGateRms)
        {
            // Rise quickly to louder speech, decay slowly so single quiet words
            // between loud ones do not swing the gain.
            if (!_heardSpeech)
            {
                // The first speech frame sets the level, so the first words are
                // already boosted instead of waiting for the estimate to settle.
                _speechLevel = rms;
                _heardSpeech = true;
            }
            else
            {
                _speechLevel += (rms - _speechLevel) * (rms > _speechLevel ? LevelAttack : LevelRelease);
            }

            var desired = Math.Clamp(TargetRms / _speechLevel, 1f, MaxGain);
            _gain += (desired - _gain) * (desired > _gain ? GainRise : 1f);
        }

        if (peak > 0 && peak * _gain > PeakCeiling)
            _gain = Math.Max(1f, PeakCeiling / peak);
    }
}
