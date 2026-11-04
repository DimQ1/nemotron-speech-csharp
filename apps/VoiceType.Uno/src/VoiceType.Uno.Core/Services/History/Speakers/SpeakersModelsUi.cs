using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.History;

/// <summary>Wording of the Models › Speakers tab, Settings › Speakers and the chat additions (testable without a UI).</summary>
public static class SpeakersModelsUi
{
    public static string TabLabel => Loc.T("Models.Tabs.Speakers");
    public const string ModelTitle = "Nemotron 3 Diarization";
    public static string Licence => Loc.T("Models.Speakers.Licence");
    public static string Facts => Loc.T("Models.Speakers.Facts");
    public static string SourceText => Loc.F("Models.Speakers.Source", "huggingface.co/DimQ1/nemotron-3-diarization-onnx");

    public static string Intro => Loc.T("Models.Speakers.Intro");

    public static string QuickPrompt => Loc.T("Models.Speakers.QuickPrompt");

    // Sent to the language model: stays English.
    public const string QuickPromptInstruction = "Who said what? Go through the recording speaker by speaker and use the speaker names as given; never invent names for \"Speaker N\".";

    public static string AfterStopHint => Loc.T("Models.Speakers.AfterStopHint");
    public static string ThreadsHintText => Loc.T("Models.Speakers.ThreadsHint");

    /// <summary>Install status line: the service's text when it has one, else a generic one.</summary>
    public static string Status(bool serviceAvailable, bool modelAvailable, string? serviceStatus)
    {
        if (!serviceAvailable)
            return Loc.T("Models.Speakers.NotInBuild");
        if (!string.IsNullOrWhiteSpace(serviceStatus))
            return serviceStatus!;
        return modelAvailable ? Loc.F("Models.Speakers.Installed", ModelTitle) : Loc.T("Models.Speakers.NotInstalled");
    }

    /// <summary>Where the model is stored and where to copy it for an offline install (the <c>diarization/</c> folder of the models root).</summary>
    public static string PlaceFilesText(ModelStorageLayout layout) =>
        Loc.F("Models.Storage.StoredAndPlace", ModelStorageText.StoredIn(ModelKind.Speaker, layout), ModelStorageText.PlaceFilesText(ModelKind.Speaker, layout));

    public static string ThreadsLabel(int threads) => threads <= 0 ? Loc.T("Models.Speakers.ThreadsAutomatic") : Loc.Plural("Models.Speakers.Threads", threads);

    public static readonly IReadOnlyList<int> ThreadChoices = [0, 1, 2, 4, 6, 8];

    public static string AfterStopLabel(SpeakerAutoMode value) => value == SpeakerAutoMode.Best ? Loc.T("Models.Speakers.AfterStopBest") : Loc.T("Models.Speakers.AfterStopOff");

    /// <summary>Citation chip text: <c>Weekly sync · Anna 03:20</c> (design §10.8); the speaker is skipped when unknown.</summary>
    public static string CitationChip(string? recordingTitle, string? speakerName, string time) =>
        string.Join(" · ", new[] { recordingTitle, string.IsNullOrWhiteSpace(speakerName) ? time : speakerName + " " + time }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
}
