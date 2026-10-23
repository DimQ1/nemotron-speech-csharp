namespace SpeechLib.Nemotron;

/// <summary>
/// Where the graphs of a Nemotron recognizer run and why: what was asked for, what the encoder runs on, and the reason
/// when those differ. The prediction network and the joint always run on the CPU, so only the encoder has a choice.
/// </summary>
/// <param name="Requested">The execution-provider name the caller passed (<c>cpu</c>, <c>webgpu:hp</c>, ...); empty when none.</param>
/// <param name="EncoderOnWebGpu">True while the encoder runs on the WebGPU execution provider.</param>
/// <param name="Adapter">The graphics adapter the encoder was set up for when <paramref name="EncoderOnWebGpu"/> is true, otherwise null.</param>
/// <param name="Reason">
/// Why the encoder is not on WebGPU although WebGPU was requested (no plugin, no discrete graphics card, a crash marker of an
/// earlier run, another WebGPU recognizer in the process, a session or run failure), or what was ignored of a request that was
/// honoured. Null when the request was carried out as given.
/// </param>
public sealed record NemotronProviderInfo(string Requested, bool EncoderOnWebGpu, string? Adapter, string? Reason)
{
    /// <summary>The provider of the encoder: <c>"cpu"</c> or <c>"webgpu"</c>.</summary>
    public string Effective => EncoderOnWebGpu ? "webgpu" : "cpu";

    /// <summary>One line for logs and diagnostics screens.</summary>
    public string Summary
    {
        get
        {
            var text = EncoderOnWebGpu
                ? $"encoder on WebGPU ({Adapter}), prediction network and joint on CPU"
                : "all graphs on CPU";
            var requested = string.IsNullOrWhiteSpace(Requested) ? "none" : Requested;
            return Reason is null ? $"{text} (requested: {requested})" : $"{text} (requested: {requested}; {Reason})";
        }
    }

    /// <summary>Everything on the CPU, as the caller asked for.</summary>
    internal static NemotronProviderInfo Cpu(string? requested, string? reason = null) => new(requested ?? "", false, null, reason);
}
