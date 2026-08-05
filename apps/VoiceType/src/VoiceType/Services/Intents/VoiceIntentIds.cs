namespace VoiceType.Services.Intents;

/// <summary>Canonical voice-command intent ids.</summary>
public static class VoiceIntentIds
{
    /// <summary>Plain dictation — recognized text is typed as-is (default).</summary>
    public const string Dictation = "dictation";
    /// <summary>"start typing" — enable text injection into the focused window.</summary>
    public const string StartTyping = "start_typing";
    /// <summary>"stop typing" — disable text injection.</summary>
    public const string StopTyping = "stop_typing";
    /// <summary>"type into &lt;app&gt;" — retarget injection to a remembered window/field.</summary>
    public const string SwitchTarget = "switch_target";
    /// <summary>"cancel" — abort the pending command / stay in listening.</summary>
    public const string Cancel = "cancel";
}
