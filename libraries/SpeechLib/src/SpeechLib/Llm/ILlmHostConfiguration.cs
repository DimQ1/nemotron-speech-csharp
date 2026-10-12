namespace SpeechLib.Llm;

/// <summary>The engine settings that can change while the application runs (model switch, CPU/GPU, context window).</summary>
/// <param name="ModelPath">Path to the model file.</param>
/// <param name="Backend">Compute backend: <c>"cpu"</c> or <c>"gpu"</c>.</param>
/// <param name="ContextTokens">Context window in tokens (prompt + answer).</param>
public sealed record LlmHostSettings(string ModelPath, string Backend, int ContextTokens);

/// <summary>
/// Runtime reconfiguration of the LLM host: the model file, backend and context window can be changed without
/// restarting the application.
/// </summary>
public interface ILlmHostConfiguration
{
    /// <summary>The settings the next engine load uses (and the current engine was loaded with).</summary>
    LlmHostSettings Settings { get; }

    /// <summary>
    /// Switches the host to <paramref name="settings"/>: interrupts the running request (Background and
    /// not-yet-answered Live requests restart on the new engine, an Interactive answer continues from the text
    /// already streamed, a Live request that already streamed text fails with <see cref="LlmUnavailableException"/>),
    /// drops every live conversation, unloads the engine and applies the new values. The next request loads the
    /// engine lazily; <see cref="ILlmHost.ContextTokens"/> reflects the new window as soon as this task completes.
    /// A prefill cannot be interrupted, so the call may wait for it.
    /// </summary>
    Task ReconfigureAsync(LlmHostSettings settings, CancellationToken cancellationToken = default);
}
