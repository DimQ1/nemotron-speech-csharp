namespace VoiceType.Uno.History;

/// <summary>Wording of the Models › Speakers tab, Settings › Speakers and the chat additions (testable without a UI).</summary>
public static class SpeakersModelsUi
{
    public const string TabLabel = "Speakers";
    public const string ModelTitle = "Nemotron 3 Diarization";
    public const string ModelFolder = "nemotron-3-diarization-onnx-int8";
    public const string Licence = "OpenMDW-1.1 · NVIDIA Nemotron-3-Diarization";
    public const string Facts = "ONNX · about 100M parameters · up to 8 speakers";
    public const string DownloadDisabledText = "Download will be available once published";

    public const string Intro = "Tells who spoke when, on this device. Run it from a recording (Detect speakers) or after each recording stops.";

    public const string QuickPrompt = "Who said what";
    public const string QuickPromptInstruction = "Who said what? Go through the recording speaker by speaker and use the speaker names as given; never invent names for \"Speaker N\".";

    public const string AfterStopHint = "Off: only when you press Detect speakers or run Reprocess";
    public const string ThreadsHintText = "How many CPU threads the model may use. Automatic is fine for most devices";

    /// <summary>Install status line: the service's text when it has one, else a generic one.</summary>
    public static string Status(bool serviceAvailable, bool modelAvailable, string? serviceStatus)
    {
        if (!serviceAvailable)
            return "Speaker detection is not part of this build";
        if (!string.IsNullOrWhiteSpace(serviceStatus))
            return serviceStatus!;
        return modelAvailable ? ModelTitle + " is installed" : "Model not installed";
    }

    /// <summary>Where to copy the model folder: the app's models folder plus the folder name.</summary>
    public static string PlaceFilesText(string modelsRoot) =>
        "Copy the model folder “" + ModelFolder + "” (model.onnx and its config files) into " + modelsRoot
        + ". It is detected automatically; use Refresh at the top of the page.";

    public static string ThreadsLabel(int threads) => threads <= 0 ? "Automatic" : threads + (threads == 1 ? " thread" : " threads");

    public static readonly IReadOnlyList<int> ThreadChoices = [0, 1, 2, 4, 6, 8];

    public static string AfterStopLabel(SpeakerAutoMode value) => value == SpeakerAutoMode.Best ? "Best quality" : "Off";

    /// <summary>Citation chip text: <c>Weekly sync · Anna 03:20</c> (design §10.8); the speaker is skipped when unknown.</summary>
    public static string CitationChip(string? recordingTitle, string? speakerName, string time) =>
        string.Join(" · ", new[] { recordingTitle, string.IsNullOrWhiteSpace(speakerName) ? time : speakerName + " " + time }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
}
