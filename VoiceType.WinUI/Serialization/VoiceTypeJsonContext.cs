using System.Text.Json.Serialization;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.Serialization;

/// <summary>
/// Source-generated JSON serialization context for all VoiceType model types.
/// Eliminates the need for reflection-based serialization, enabling trimming.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PostProcessingRule))]
[JsonSerializable(typeof(RecognitionSession))]
[JsonSerializable(typeof(List<PostProcessingRule>))]
[JsonSerializable(typeof(List<RecognitionSession>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class VoiceTypeJsonContext : JsonSerializerContext
{
}
