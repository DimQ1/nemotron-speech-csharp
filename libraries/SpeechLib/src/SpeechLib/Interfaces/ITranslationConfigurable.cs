namespace SpeechLib;

/// <summary>Optional capability for recognizers that can translate audio through their own prompt.</summary>
public interface ITranslationConfigurable
{
    /// <summary>Enables or disables prompt translation and selects its target language.</summary>
    bool TrySetTranslation(bool enabled, string targetLanguage);
}