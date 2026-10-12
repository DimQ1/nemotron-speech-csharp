using SpeechLib.Llm;

namespace VoiceType.Uno.Services.Jobs;

/// <summary>Whether the on-device Gemma engine can run background work (titles, translation, summaries).</summary>
internal static class GemmaAvailability
{
    /// <summary>The engine host, or null when this build has none.</summary>
    public static ILlmHost? Host => App.Services.GetService<ILlmHost>();

    /// <summary>The model file is installed and a host exists to run it.</summary>
    public static bool IsInstalled => Host?.IsModelInstalled == true;
}
