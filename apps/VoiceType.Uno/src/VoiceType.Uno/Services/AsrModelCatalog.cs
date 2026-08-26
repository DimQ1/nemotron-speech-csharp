using SpeechLib.ModelDownload;

namespace VoiceType.Uno.Services;

/// <summary>Compatibility shim — the catalog now lives in SpeechLib.ModelDownload.</summary>
public static class AsrModelCatalog
{
    public static IReadOnlyList<ModelDescriptor> Models => ModelCatalog.Models;
    public static ModelDescriptor Recommended => ModelCatalog.Recommended;
}
