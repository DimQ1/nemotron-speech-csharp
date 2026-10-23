namespace SpeechLib.Audio;

/// <summary>Parameters of <see cref="AutomaticGainControl"/>; the defaults are the measured ones.</summary>
public sealed record AutomaticGainControlOptions
{
    /// <summary>The defaults.</summary>
    public static AutomaticGainControlOptions Default { get; } = new();

    /// <summary>Speech RMS the gain aims for (default about -30 dBFS).</summary>
    public float TargetRms { get; init; } = AutomaticGainControl.TargetRms;

    /// <summary>Largest boost (default 20 = +26 dB).</summary>
    public float MaxGain { get; init; } = AutomaticGainControl.MaxGain;

    /// <summary>
    /// Highest RMS the measured noise floor is raised to (default about -50 dBFS). The boost is capped at this level divided by the
    /// floor, so a room tone is never lifted into the level range the model takes for speech; a quiet room leaves the full boost.
    /// 0 or less removes the cap.
    /// </summary>
    public float NoiseCeilingRms { get; init; } = AutomaticGainControl.NoiseCeilingRms;
}

/// <summary>
/// Slow automatic gain control for recognizer input. It raises quiet speech toward a target level and never attenuates below
/// unity, so normally leveled audio passes through unchanged.
/// </summary>
/// <remarks>
/// <para>Some models have no input normalization and return nothing at all for quiet microphones: the Nemotron streaming model is
/// silent at a -40 dBFS peak, where Parakeet (per-feature normalization) still transcribes correctly.</para>
/// <para><b>Speech, not sound, sets the gain.</b> The noise floor is the 10th percentile of the 10 ms frame levels of the last two
/// seconds (digital silence is no information about the room and is skipped). Speech is recognized by its syllables: a burst that
/// starts 12 dB above the floor, may stay down to 6 dB above it (hysteresis) and ends within a second, by falling 8 dB under its
/// own peak or to the floor. A room tone never starts a burst, a click is too short to be a syllable and a steady sound (a room
/// tone that a fan or a splice raised by a step) does not end, so none of them sets the level. The rule this replaces let the first
/// frame above a fixed -60 dBFS gate set the level and followed every frame above it, which lifted a -54 dBFS room tone by 26 dB
/// in every pause and made the model miss the sentences that followed.</para>
/// <para><b>The first syllable is boosted as it comes, on probation.</b> The model drops a whole utterance whose level jumps by
/// 15 dB or more after its first word, so the gain cannot wait for the first syllable to end: within the first burst it follows the
/// speech frame by frame from the third frame on (the loudest of the first three sets the level, louder frames lower the gain at
/// once; speech that needs less than +6 dB is not raised yet). A burst that turns out to be a click (fewer than 50 ms above the
/// speech margin) or a steady sound (no dip within half a second) takes its boost back; the first syllable keeps it.</para>
/// <para><b>Then the gain never rises in a pause.</b> Louder speech lowers it at once, frame by frame; the quieter frames of a
/// syllable lower the level (and so raise the gain, slowly) only when the syllable has ended, so a pause or a steady sound keeps the
/// gain it had instead of pumping a room tone up.</para>
/// <para><b>Room tone stays below the model's speech range.</b> The boost never lifts the measured floor above
/// <see cref="AutomaticGainControlOptions.NoiseCeilingRms"/>; in a quiet room this leaves the full <see cref="AutomaticGainControlOptions.MaxGain"/>.
/// A frame whose peak would clip lowers the gain at once.</para>
/// <para>The state describes the microphone and the room: nothing resets it but a new instance.</para>
/// </remarks>
public sealed class AutomaticGainControl
{
    /// <summary>
    /// Default speech RMS the gain aims for (about -30 dBFS, measured on the level envelope of the syllables). The rule before G1
    /// aimed at -24 dBFS, which raised a quarter of the Common Voice recordings that the model reads as well unprocessed; -30 dBFS
    /// keeps the quiet-microphone rescue (measured down to a -46 dBFS peak) and leaves those alone, -34 dBFS rescues less.
    /// </summary>
    public const float TargetRms = 0.03f;

    /// <summary>Default largest boost (+26 dB).</summary>
    public const float MaxGain = 20f;

    /// <summary>Default highest level the noise floor is raised to (about -50 dBFS RMS).</summary>
    public const float NoiseCeilingRms = 0.00316f;

    /// <summary>Frames quieter than this RMS (about -70 dBFS) are never speech, however low the noise floor is.</summary>
    public const float MinSpeechRms = 0.0003f;

    private const float PeakCeiling = 0.9f;
    private const float LevelAttack = 0.5f;
    private const float LevelRelease = 0.02f;
    private const float GainRise = 0.05f;
    private const double SpeechMarginDb = 12;            // a burst starts this far above the floor...
    private const double HoldMarginDb = 6;               // ...goes on while its frames stay this far above it...
    private const double DipDb = 8;                      // ...and above its own peak minus this
    private const int MinSyllableFrames = 5;             // a burst with fewer frames above the speech margin is a click
    private const int MaxSyllableFrames = 100;           // a burst that has not ended after a second is a steady sound...
    private const int ProbationFrames = 50;              // ...and a first burst after half a second (its boost is taken back sooner)
    private const float ProbationMinGain = 2f;           // a first burst is raised only when it needs +6 dB or more...
    private const int ProbationOnsetFrames = 3;          // ...from its third frame above the speech margin on
    private const double DigitalSilence = 1e-10;         // mean square of -100 dBFS: a muted or not yet running source
    private const int FloorFrames = 200;                 // the floor is a low percentile of the last 2 s...
    private const double FloorPercentile = 0.1;          // ...the 10th, so a few near-silent frames (a fade, a splice) cannot pull it down
    private const int FloorUpdateFrames = 5;             // recomputed every 50 ms

    private static readonly double EnterRatio = Math.Pow(10, SpeechMarginDb / 10);
    private static readonly double HoldRatio = Math.Pow(10, HoldMarginDb / 10);
    private static readonly double DipRatio = Math.Pow(10, DipDb / 10);

    private readonly AutomaticGainControlOptions _options;
    private readonly int _frameSamples;
    private readonly double _ceilingSquare;

    private double _frameSumSquares;
    private float _framePeak;
    private int _frameCount;

    // noise floor (mean square)
    private readonly double[] _levels = new double[FloorFrames];
    private readonly double[] _sorted = new double[FloorFrames];
    private int _levelCount;
    private int _levelIndex;
    private int _sinceFloorUpdate;
    private double _floor;

    // the burst in progress; after the first syllable its quieter frames wait until it has ended as a syllable
    private bool _inBurst;
    private bool _steady;
    private bool _probation;
    private int _burstFrames;
    private int _burstStrong;
    private double _burstPeak;
    private readonly float[] _burstLevels = new float[MaxSyllableFrames];
    private int _burstLevelCount;

    // speech
    private bool _heardSpeech;
    private bool _hasLevel;
    private float _speechLevel;
    private float _gain = 1f;

    /// <summary>A gain control with the default parameters.</summary>
    public AutomaticGainControl(int sampleRate = 16000)
        : this(sampleRate, AutomaticGainControlOptions.Default)
    {
    }

    /// <summary>A gain control with the given parameters.</summary>
    public AutomaticGainControl(int sampleRate, AutomaticGainControlOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _frameSamples = Math.Max(1, sampleRate / 100);
        _ceilingSquare = options.NoiseCeilingRms > 0 ? (double)options.NoiseCeilingRms * options.NoiseCeilingRms : 0;
    }

    /// <summary>Gain currently applied.</summary>
    public float Gain => _gain;

    /// <summary>The measured noise floor (RMS); 0 before the first frame with sound.</summary>
    public float NoiseFloorRms => (float)Math.Sqrt(_floor);

    /// <summary>The level of the speech heard so far (RMS); 0 before the first syllable.</summary>
    public float SpeechRms => _hasLevel ? _speechLevel : 0f;

    /// <summary>True once a syllable has been heard: the gain is no longer on probation.</summary>
    public bool HasHeardSpeech => _heardSpeech;

    /// <summary>
    /// Returns a new buffer with the gain applied; <paramref name="input"/> is not changed. NaN and infinite samples
    /// come out as 0 and do not reach the level estimate.
    /// </summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new float[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var sample = input[i];
            if (!float.IsFinite(sample))
                sample = 0f;   // NaN or Infinity from a faulty source: silence; Infinity would make the level estimate NaN for good
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
        var meanSquare = _frameSumSquares / _frameCount;
        var rms = (float)Math.Sqrt(meanSquare);
        var peak = _framePeak;
        _frameSumSquares = 0;
        _framePeak = 0;
        _frameCount = 0;

        var sound = meanSquare >= DigitalSilence;
        if (sound)
            UpdateFloor(meanSquare);

        var audible = sound && rms >= MinSpeechRms;
        var strong = audible && meanSquare >= _floor * EnterRatio;
        if (_inBurst)
        {
            if (!audible || meanSquare < _floor * HoldRatio || meanSquare * DipRatio <= _burstPeak)
                EndBurst();
            else
                ContinueBurst(meanSquare, rms, strong);
        }
        if (!_inBurst && strong)
            StartBurst(meanSquare, rms);

        // A room that got louder lowers the ceiling at once, also in a pause.
        var ceiling = CeilingGain();
        if (_gain > ceiling)
            _gain = ceiling;

        if (peak > 0 && peak * _gain > PeakCeiling)
            _gain = Math.Max(1f, PeakCeiling / peak);
    }

    private void StartBurst(double meanSquare, float rms)
    {
        _inBurst = true;
        _steady = false;
        _probation = !_heardSpeech;
        _burstFrames = 0;
        _burstPeak = 0;
        _burstStrong = 0;
        _burstLevelCount = 0;
        ContinueBurst(meanSquare, rms, strong: true);
    }

    private void ContinueBurst(double meanSquare, float rms, bool strong)
    {
        _burstFrames++;
        if (meanSquare > _burstPeak)
            _burstPeak = meanSquare;
        if (_steady)
            return;
        if (_burstFrames > (_probation ? ProbationFrames : MaxSyllableFrames))
        {
            // no syllable lasts that long: a steady sound, its frames never count and a boost on probation is taken back
            _steady = true;
            if (_probation)
                TakeBackProbation();
            return;
        }
        if (!strong)
            return;

        _burstStrong++;
        if (!_probation)
        {
            if (rms > _speechLevel)
            {
                // Louder speech lowers the gain at once: waiting for the end of the syllable would play the first word of a louder
                // talker (or the first word after a click that was taken for a syllable) 10 to 20 dB too loud, the peak limiter
                // only keeps it from clipping. Lowering the gain on a sound that is not speech costs nothing.
                _speechLevel += (rms - _speechLevel) * LevelAttack;
                _gain = Math.Min(_gain, DesiredGain());
            }
            else
            {
                _burstLevels[_burstLevelCount++] = rms;     // quieter frames move the level once the syllable has ended
            }
            return;
        }

        _burstLevels[_burstLevelCount++] = rms;
        if (_burstStrong >= ProbationOnsetFrames)
        {
            // The first burst is boosted as it comes. Its loudest of the first three frames sets the level (a single onset frame is
            // quieter than the syllable and would overshoot, a blip of the room shorter than that is never raised), every frame after
            // that follows, louder ones lower the gain at once. Speech that needs less than +6 dB is left alone until it has proven
            // to be speech.
            var first = _burstStrong == ProbationOnsetFrames;
            if (first)
            {
                _speechLevel = 0f;
                for (var i = 0; i < ProbationOnsetFrames; i++)
                    _speechLevel = Math.Max(_speechLevel, _burstLevels[i]);
                _hasLevel = true;
            }
            else
            {
                UpdateLevel(rms);
            }
            var desired = DesiredGain();
            if (desired < ProbationMinGain)
                desired = 1f;
            _gain = first ? desired : _gain + (desired - _gain) * (desired > _gain ? GainRise : 1f);
        }
    }

    private void EndBurst()
    {
        _inBurst = false;
        var syllable = !_steady && _burstStrong >= MinSyllableFrames;
        if (_probation)
        {
            _probation = false;
            if (syllable)
                _heardSpeech = true;            // the first syllable: its boost stays
            else if (!_steady)
                TakeBackProbation();            // a click (a steady sound was taken back when it was recognized)
            return;
        }
        if (!syllable)
            return;

        // a later syllable lets its quieter frames lower the level once it has ended, which raises the gain slowly
        for (var i = 0; i < _burstLevelCount; i++)
            UpdateLevel(_burstLevels[i]);
        var desired = DesiredGain();
        _gain = desired > _gain ? _gain + (desired - _gain) * (1f - MathF.Pow(1f - GainRise, _burstStrong)) : desired;
    }

    // Before the first syllable the gain is 1 and there is no level: back to that.
    private void TakeBackProbation()
    {
        _hasLevel = false;
        _speechLevel = 0f;
        _gain = 1f;
    }

    private void UpdateLevel(float rms)
    {
        if (!_hasLevel)
        {
            _speechLevel = rms;
            _hasLevel = true;
        }
        else
        {
            // Rise quickly to louder speech, decay slowly so single quiet words between loud ones do not swing the gain.
            _speechLevel += (rms - _speechLevel) * (rms > _speechLevel ? LevelAttack : LevelRelease);
        }
    }

    private float DesiredGain() => Math.Min(Math.Clamp(_options.TargetRms / _speechLevel, 1f, _options.MaxGain), CeilingGain());

    // The boost that lifts the noise floor exactly to the ceiling; never below unity, unlimited without a ceiling or a floor.
    private float CeilingGain()
    {
        if (_ceilingSquare <= 0 || _floor <= 0)
            return _options.MaxGain;
        return (float)Math.Max(1.0, Math.Sqrt(_ceilingSquare / _floor));
    }

    // The floor is the 10th percentile of the frame levels of the last two seconds: it follows a quieter room within 0.2 s and a
    // louder one within 1.8 s, the pauses of speech keep it down, and the few near-silent frames at the edge of a recording or a
    // splice (a minimum would take them) do not make a room tone look like speech.
    private void UpdateFloor(double meanSquare)
    {
        _levels[_levelIndex] = meanSquare;
        _levelIndex = (_levelIndex + 1) % FloorFrames;
        if (_levelCount < FloorFrames)
            _levelCount++;

        if (_floor > 0 && ++_sinceFloorUpdate < FloorUpdateFrames)
            return;
        _sinceFloorUpdate = 0;
        Array.Copy(_levels, _sorted, _levelCount);
        Array.Sort(_sorted, 0, _levelCount);
        _floor = _sorted[(int)(FloorPercentile * (_levelCount - 1))];
    }
}
