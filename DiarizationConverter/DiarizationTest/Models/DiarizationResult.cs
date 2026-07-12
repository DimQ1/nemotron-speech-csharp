namespace DiarizationTest.Models;

/// <summary>
/// A single speaker segment with start time, end time, and speaker label.
/// </summary>
public readonly record struct SpeakerSegment(
    string SpeakerId,
    double StartSeconds,
    double EndSeconds
);
