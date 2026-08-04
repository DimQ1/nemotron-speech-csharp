namespace SpeechLib;

/// <summary>
/// Optional capability exposed by recognizers that can change language without
/// rebuilding the model session.
/// </summary>
public interface ILanguageConfigurable
{
    /// <summary>Attempts to apply a model-specific language identifier.</summary>
    bool TrySetLanguage(string language);
}