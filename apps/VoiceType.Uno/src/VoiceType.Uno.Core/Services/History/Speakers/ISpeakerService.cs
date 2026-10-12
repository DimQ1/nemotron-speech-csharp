namespace VoiceType.Uno.History;

public sealed record SpeakerPerson(string Id, string Name, int Color);        // Color = palette index 0..7
public sealed record SpeakerStat(string PersonId, double TalkSeconds, double Percent, int Turns);
public sealed record SegmentSpeakers(int SegmentIndex, string? PersonId, bool HasChange, bool HasOverlap, IReadOnlyList<string?> WordPersonIds);   // PersonId = majority speaker; WordPersonIds one per word (null = unknown)
public sealed record SpeakerLayerView(string LayerId, string RunId, string VersionId, string ModelTitle, string Preset,
    IReadOnlyList<SpeakerPerson> People, IReadOnlyList<SegmentSpeakers> Segments, IReadOnlyList<SpeakerStat> Stats);
public interface ISpeakerService
{
    bool IsModelAvailable { get; }                       // the ONNX model folder is installed
    string ModelStatus { get; }                          // human text: "Nemotron 3 Diarization INT8 · 106 MB" / "Model not installed"
    SpeakerLayerView? GetLayer(string recordingId, string versionId);     // current layer of that version, null if none
    string? EnqueueDiarize(string recordingId, string versionId, bool runNow);   // returns the job id (null if unavailable/duplicate)
    void RenamePerson(string recordingId, string personId, string name);
    void SetPersonColor(string recordingId, string personId, int color);
    void MergePeople(string recordingId, string intoPersonId, string fromPersonId);
    void SplitPerson(string recordingId, string personId);                 // undo of a merge
    event Action<string>? Changed;                       // recordingId; any thread
}
