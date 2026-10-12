namespace VoiceType.Uno.History;

/// <summary>
/// "Include speaker names" option of the export sheet. <see cref="Apply"/> returns a copy of a transcript whose
/// segment texts start with the speaker (<c>Anna: …</c>), which gives <c>.txt</c> as <c>[00:03:20] Anna: …</c>
/// and <c>.srt</c> / <c>.lrc</c> with an <c>Anna: </c> prefix without changing the exporters. A Core exporter
/// that wants to do this itself can use <see cref="NameFor"/> instead.
/// </summary>
public sealed record SpeakerExportOptions(bool IncludeNames, IReadOnlyDictionary<int, string> NameBySegment)
{
    public static readonly SpeakerExportOptions None = new(false, new Dictionary<int, string>());

    /// <summary>Names per segment index from a layer; <see cref="None"/> when there is no layer.</summary>
    public static SpeakerExportOptions From(SpeakerLayerView? layer, bool includeNames)
    {
        if (layer is null || !includeNames)
            return None;
        var map = new Dictionary<int, string>();
        foreach (var s in layer.Segments)
        {
            if (SpeakersUi.PersonOf(layer, s.PersonId)?.Name is { Length: > 0 } name)
                map[s.SegmentIndex] = name;
        }
        return new SpeakerExportOptions(true, map);
    }

    public string? NameFor(int segmentIndex) =>
        IncludeNames && NameBySegment.TryGetValue(segmentIndex, out var name) ? name : null;

    /// <summary>Copy of <paramref name="transcript"/> with "Name: " in front of every labelled segment; the same instance when off.</summary>
    public TranscriptDocument Apply(TranscriptDocument transcript)
    {
        if (!IncludeNames || NameBySegment.Count == 0)
            return transcript;
        var copy = new TranscriptDocument
        {
            Schema = transcript.Schema,
            Version = transcript.Version,
            Segments = [],
        };
        for (var i = 0; i < transcript.Segments.Count; i++)
        {
            var s = transcript.Segments[i];
            var text = s.Text.Trim();
            if (NameFor(s.Index) is { } name && text.Length > 0)
                text = name + ": " + text;
            copy.Segments.Add(new TranscriptSegment
            {
                Index = s.Index,
                Start = s.Start,
                End = s.End,
                Text = text,
                Words = s.Words,
                WordTiming = s.WordTiming,
            });
        }
        copy.Text = TranscriptDocument.JoinSegments(copy.Segments);
        return copy;
    }
}
