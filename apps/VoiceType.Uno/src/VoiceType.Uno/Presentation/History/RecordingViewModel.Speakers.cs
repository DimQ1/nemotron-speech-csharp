using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

/// <summary>Speaker state of one transcript segment (design §10.8): name, colour, header, filter, word runs.</summary>
public sealed partial class SegmentViewModel
{
    private SpeakerRowInfo? _row;
    private SegmentSpeakers? _labels;
    private SpeakerLayerView? _layer;
    private IReadOnlyList<SpeakerTextRun> _runs = [];
    private int _stamp;

    public string WordTimingKind { get; private init; } = WordTimingKinds.Estimated;

    /// <summary>Raised-with-change counter the text control watches.</summary>
    public int SpeakerStamp => _stamp;

    public string? SpeakerId => _row?.PersonId;
    public bool HasSpeaker => _row?.PersonId is not null;
    public string SpeakerName => SpeakersUi.NameOf(_layer, _row?.PersonId);

    public int SpeakerColor => SpeakersUi.PersonOf(_layer, _row?.PersonId)?.Color ?? -1;

    /// <summary>First segment of a run by one speaker: the name line is drawn above it.</summary>
    public bool ShowHeader => _row?.ShowHeader == true && !IsHidden;

    public bool IsHidden => _row?.IsHidden == true;
    public bool ShowRow => !IsHidden;
    public string CollapsedText => _row?.CollapsedText ?? "";
    public bool HasCollapsed => CollapsedText.Length > 0;
    public string SpeakerPrefix => HasSpeaker ? SpeakerName + ", " : "";
    public string HeaderAutomationName => $"{SpeakerName}, rename speaker";

    public bool HasOverlap => _labels?.HasOverlap == true;

    /// <summary>The speaker changes inside this segment and the word timing is only estimated: the split may be off by a word or two.</summary>
    public bool HasApproximateChange =>
        _labels is not null && SpeakersUi.HasWordChange(_labels) && WordTimingKind != WordTimingKinds.Decoder;

    public bool HasMarkers => HasOverlap || HasApproximateChange;

    public string MarkerText => HasOverlap && HasApproximateChange ? "Overlapping speech · approximate split"
        : HasOverlap ? "Overlapping speech"
        : HasApproximateChange ? "Speaker split is approximate"
        : "";

    public IReadOnlyList<SpeakerTextRun> TextRuns => _runs;

    public void ApplySpeakers(SpeakerRowInfo? row, SegmentSpeakers? labels, SpeakerLayerView? layer, bool colorWords)
    {
        _row = row;
        _labels = labels;
        _layer = layer;
        _runs = SpeakersUi.BuildRuns(Text, labels, layer, colorWords);
        _stamp++;
        foreach (var name in new[]
                 {
                     nameof(SpeakerStamp), nameof(SpeakerId), nameof(HasSpeaker), nameof(SpeakerName), nameof(SpeakerColor),
                     nameof(ShowHeader), nameof(IsHidden), nameof(ShowRow), nameof(CollapsedText), nameof(HasCollapsed),
                     nameof(SpeakerPrefix), nameof(HeaderAutomationName), nameof(HasOverlap), nameof(HasApproximateChange),
                     nameof(HasMarkers), nameof(MarkerText), nameof(TextRuns), nameof(AutomationName),
                 })
            OnPropertyChanged(name);
    }
}

/// <summary>Versions tab: the diarization model and mode of the version's speaker layer.</summary>
public sealed partial class VersionCardViewModel
{
    public string SpeakersText { get; private set; } = "";
    public string PresetText { get; private set; } = "";
    public bool HasSpeakers => SpeakersText.Length > 0;
    public bool HasPreset => PresetText.Length > 0;

    public void SetSpeakers(SpeakerLayerView? layer)
    {
        SpeakersText = layer is null ? "" : SpeakersUi.VersionLine(layer);
        PresetText = layer is null ? "" : SpeakersUi.PresetLine(layer);
    }
}

/// <summary>Recording page: the speaker layer of the viewed version, the filter, word colours and the "Detect speakers" state.</summary>
public sealed partial class RecordingViewModel
{
    private readonly Dictionary<string, SpeakerLayerView?> _layers = new(StringComparer.Ordinal);
    private SpeakerLayerView? _layer;
    private string? _filterPersonId;
    private bool _colorWords;
    private SpeakerDetectInfo _detect = SpeakerDetectUi.Describe(false, false, false, false, null);

    /// <summary>Speaker state changed (layer, filter, colours, detection): the page redraws its speaker bar.</summary>
    public event Action? SpeakersChanged;

    public SpeakerLayerView? Layer => _layer;
    public bool HasLayer => _layer is not null;
    public bool HasWordLabels => SpeakersUi.HasWordLabels(_layer);
    public SpeakerDetectInfo Detect => _detect;
    public string? FilterPersonId => _filterPersonId;
    public bool IsFilterActive => _filterPersonId is not null && _layer is not null;
    public bool IsSpeakerBarVisible => IsTranscriptTab && (HasLayer || _detect.IsVisible);

    public bool ColorWords
    {
        get => _colorWords;
        set
        {
            if (_colorWords == value)
                return;
            _colorWords = value;
            SpeakerServices.SetColorWords(RecordingId, value);
            ApplySpeakers();
        }
    }

    public string FilterLine
    {
        get
        {
            if (!IsFilterActive)
                return "";
            var rows = SpeakersUi.BuildRows(_layer, Segments.Count, _filterPersonId);
            return SpeakersUi.FilterLine(SpeakersUi.NameOf(_layer, _filterPersonId), SpeakersUi.CountShown(rows), Segments.Count);
        }
    }

    public int FilterColor => SpeakersUi.PersonOf(_layer, _filterPersonId)?.Color ?? -1;

    public SpeakerLayerView? LayerOf(string versionId) => _layers.GetValueOrDefault(versionId);

    /// <summary>Reads the layers of every version off the UI thread; keeps a filter only while its person exists.</summary>
    private async Task LoadSpeakersAsync(string recordingId)
    {
        var service = SpeakerServices.Current;
        var versions = _manifest?.Versions.Select(v => v.Id).ToList() ?? [];
        var layers = service is null
            ? new Dictionary<string, SpeakerLayerView?>()
            : await Task.Run(() =>
            {
                var map = new Dictionary<string, SpeakerLayerView?>(StringComparer.Ordinal);
                foreach (var id in versions)
                {
                    try
                    {
                        map[id] = service.GetLayer(recordingId, id);
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
                    {
                        Console.WriteLine($"[VoiceType.Uno] Speaker layer of {id} could not be read: {ex.Message}");
                    }
                }
                return map;
            });
        _layers.Clear();
        foreach (var (key, value) in layers)
            _layers[key] = value;
        _layer = _viewedVersionId is { } viewed ? _layers.GetValueOrDefault(viewed) : null;
        _colorWords = SpeakerServices.ColorWords(recordingId);
        if (_filterPersonId is not null && SpeakersUi.PersonOf(_layer, _filterPersonId) is null)
            _filterPersonId = null;
        if (_layer is not null)
            SpeakerServices.ClearTracked(recordingId);
        ApplySpeakers(raise: false);
    }

    /// <summary>Re-reads the layers after the speaker service (or the queue) changed.</summary>
    public async Task RefreshSpeakersAsync()
    {
        if (_manifest is null)
            return;
        await LoadSpeakersAsync(RecordingId);
        BuildVersions();
        RaiseSpeakers();
    }

    public void SetFilter(string? personId)
    {
        _filterPersonId = personId is not null && SpeakersUi.PersonOf(_layer, personId) is not null ? personId : null;
        ApplySpeakers();
    }

    /// <summary>Recomputes the detection row (job progress changed).</summary>
    public void RefreshDetect()
    {
        var before = _detect;
        ComputeDetect();
        if (_detect != before)
            RaiseSpeakers();
    }

    private void ApplySpeakers(bool raise = true)
    {
        var rows = SpeakersUi.BuildRows(_layer, Segments.Count, _filterPersonId);
        var labels = new Dictionary<int, SegmentSpeakers>();
        if (_layer is not null)
        {
            foreach (var s in _layer.Segments)
                labels[s.SegmentIndex] = s;
        }
        for (var i = 0; i < Segments.Count; i++)
            Segments[i].ApplySpeakers(_layer is null ? null : rows[i], labels.GetValueOrDefault(i), _layer, _colorWords);
        ComputeDetect();
        if (raise)
            RaiseSpeakers();
    }

    private void ComputeDetect()
    {
        var service = SpeakerServices.Current;
        _detect = SpeakerDetectUi.Describe(service is not null, service?.IsModelAvailable == true, HasLayer, HasAudio,
            service is null || HasLayer ? null : SpeakerServices.TrackedJob(RecordingId));
    }

    private void RaiseSpeakers()
    {
        ComputeDetect();
        foreach (var name in new[]
                 {
                     nameof(Layer), nameof(HasLayer), nameof(HasWordLabels), nameof(Detect), nameof(FilterPersonId),
                     nameof(IsFilterActive), nameof(IsSpeakerBarVisible), nameof(ColorWords), nameof(FilterLine), nameof(FilterColor),
                 })
            OnPropertyChanged(name);
        SpeakersChanged?.Invoke();
    }

    /// <summary>Segment starts (seconds), the base of turn navigation.</summary>
    public IReadOnlyList<double> SegmentStarts => [.. Segments.Select(s => s.Start)];

    /// <summary>Turn starts of the filtered speaker; empty without a filter.</summary>
    public IReadOnlyList<double> FilterTurnStarts =>
        _layer is not null && _filterPersonId is not null ? SpeakersUi.TurnStarts(_layer, SegmentStarts, _filterPersonId) : [];
}
