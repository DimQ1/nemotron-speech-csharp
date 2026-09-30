using System.Text.Json.Serialization;

namespace SpeechLib;

/// <summary>
/// Nullable mirror of the ONNX Runtime GenAI generator search options: only the
/// properties a caller actually set are serialized and applied.
/// </summary>
public sealed class GeneratorParamsArgs
{
    // In case the user doesn't provide the batch size, set it to 1
    public int batch_size { get; set; } = 1;
    // In case the user doesn't provide the chunk size, set it to 0
    public int chunk_size { get; set; } = 0;
    public bool? do_sample { get; set; }
    public int? min_length { get; set; }
    public int? max_length { get; set; }
    // In case the user doesn't provide the number of beams, set it to 1
    public int num_beams { get; set; } = 1;
    // In case the user doesn't provide the number of return sequences, set it to 1
    public int num_return_sequences { get; set; } = 1;
    public double? repetition_penalty { get; set; }
    public double? temperature { get; set; }
    public int? top_k { get; set; }
    public double? top_p { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GeneratorParamsArgs))]
public sealed partial class ArgsSerializerContext : JsonSerializerContext
{
}
