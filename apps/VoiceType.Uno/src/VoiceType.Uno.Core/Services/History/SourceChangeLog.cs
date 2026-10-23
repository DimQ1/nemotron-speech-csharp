namespace VoiceType.Uno.History;

/// <summary>
/// The audio source switches of one recording (it changed while recording, on the fly), as (file position, new source).
/// Switches that cancel out are not kept: a source that failed to start is followed at once by the source it replaced,
/// two changes with no audio in between, which together change nothing.
/// </summary>
public sealed class SourceChangeLog(string startSource)
{
    private readonly List<(long AtSample, string Source)> _changes = [];

    /// <summary>The changes in order, each at the file position (samples of audio written so far) where it happened.</summary>
    public IReadOnlyList<(long AtSample, string Source)> Changes => _changes;

    /// <param name="atSample">Samples of audio written to the recording so far.</param>
    /// <param name="source">The source the audio after this point comes from.</param>
    public void Record(long atSample, string source)
    {
        // Two changes at the same position had no audio between them: only the later one counts.
        if (_changes.Count > 0 && _changes[^1].AtSample == atSample)
            _changes.RemoveAt(_changes.Count - 1);

        var before = _changes.Count > 0 ? _changes[^1].Source : startSource;
        if (string.Equals(before, source, StringComparison.OrdinalIgnoreCase))
            return;

        _changes.Add((atSample, source));
    }
}
