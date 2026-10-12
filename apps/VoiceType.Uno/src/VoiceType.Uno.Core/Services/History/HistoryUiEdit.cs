namespace VoiceType.Uno.History;

/// <summary>
/// State of the transcript edit mode: the source version's segment texts, the edited
/// texts, which segments changed and an undo stack. Saving passes <see cref="Texts"/> to
/// <see cref="RecordingStore.SaveEdit(string,string,IReadOnlyList{string})"/>.
/// </summary>
public sealed class HistoryUiEditSession
{
    private readonly string[] _original;
    private readonly string[] _current;
    private readonly Stack<(int Index, string Previous)> _undo = new();

    public HistoryUiEditSession(string sourceVersionId, IEnumerable<string> segmentTexts, string nextVersionId)
    {
        SourceVersionId = sourceVersionId;
        NextVersionId = nextVersionId;
        _original = segmentTexts.ToArray();
        _current = (string[])_original.Clone();
    }

    public string SourceVersionId { get; }

    /// <summary>Id the saved edit will get (for the caption).</summary>
    public string NextVersionId { get; }

    public int Count => _original.Length;

    public IReadOnlyList<string> Texts => _current;

    public string Original(int index) => _original[index];

    public bool IsChanged(int index) => !SameText(_original[index], _current[index]);

    public int ChangedCount => Enumerable.Range(0, _original.Length).Count(IsChanged);

    public bool HasChanges => ChangedCount > 0;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Records a new text for a segment; returns false when it did not change.</summary>
    public bool Set(int index, string text)
    {
        text ??= "";
        if (string.Equals(_current[index], text, StringComparison.Ordinal))
            return false;
        _undo.Push((index, _current[index]));
        _current[index] = text;
        return true;
    }

    /// <summary>Reverts the last change; returns the segment index, or -1 when there is nothing to undo.</summary>
    public int Undo()
    {
        if (_undo.Count == 0)
            return -1;
        var (index, previous) = _undo.Pop();
        _current[index] = previous;
        return index;
    }

    /// <summary><c>2 segments changed</c> / <c>No changes</c>.</summary>
    public string ChangedText => ChangedCount == 0 ? "No changes" : HistoryUiFormat.Count(ChangedCount, "segment") + " changed";

    /// <summary><c>Editing v2 · Save creates v3 · edited; v2 stays as it is.</c></summary>
    public string Caption => $"Editing {SourceVersionId} · Save creates {NextVersionId} · edited; {SourceVersionId} stays as it is.";

    /// <summary>Next free version id of a manifest (<c>v1, v2</c> → <c>v3</c>).</summary>
    public static string NextVersionIdOf(RecordingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return RecordingStore.NextId(manifest.Versions.Select(v => v.Id), "v");
    }

    // Whitespace-only differences do not create a version.
    private static bool SameText(string a, string b) =>
        string.Equals(string.Join(' ', TextTokens.SplitWords(a)), string.Join(' ', TextTokens.SplitWords(b)), StringComparison.Ordinal);
}
