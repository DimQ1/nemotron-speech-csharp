using System.Runtime.CompilerServices;
using LiteRtLmSharp;

namespace SpeechLib.LiteRT.Native;

/// <summary>
/// Translates text using a Gemma 4 model in <c>.litertlm</c> format loaded
/// in-process through LiteRT-LM (via LiteRtLmSharp) — no HTTP server required.
/// </summary>
/// <remarks>
/// The engine is created once and reused; every translation runs on its own
/// conversation so history and configuration never leak between sentences.
/// Inference is serialized through an internal semaphore because CPU decode is
/// the bottleneck and a single engine should not interleave decode passes.
/// </remarks>
public sealed class LiteRTLmNativeTranslator : ITextTranslator
{
    private readonly LiteRTLmNativeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LiteRtEngine _engine;

    public LiteRTLmNativeTranslator(LiteRTLmNativeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new ArgumentException("Model path is required.", nameof(options));

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException("LiteRT-LM model file not found.", options.ModelPath);

        LiteRtEngine.SetMinLogLevel((int)options.LogLevel);

        _engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = options.ModelPath,
            Backend = LiteRtBackend.Parse(options.Backend),
            NumThreads = options.NumThreads > 0 ? options.NumThreads : null,
            MaxNumTokens = options.MaxContextTokens > 0 ? options.MaxContextTokens : 4096,
            Cache = options.Cache,
        });
    }

    /// <inheritdoc />
    public async Task<string?> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var conversation = CreateConversation(targetLang, sourceLang);
            var response = await conversation
                .SendAsync(text, cancellationToken)
                .ConfigureAwait(false);

            var result = response.Text?.Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var conversation = CreateConversation(targetLang, sourceLang);
            await foreach (var chunk in conversation
                .SendStreamingAsync(text, cancellationToken)
                .ConfigureAwait(false))
            {
                if (chunk.Kind != LiteRtStreamChunkKind.Answer || chunk.Text.Length == 0)
                    continue;

                yield return chunk.Text;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private LiteRtConversation CreateConversation(string targetLang, string? sourceLang)
    {
        return _engine.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = _options.BuildSystemPrompt(targetLang, sourceLang),
            EnableThinking = false,
            MaxOutputTokens = _options.MaxTokens,
        });
    }

    public void Dispose()
    {
        _engine.Dispose();
        _gate.Dispose();
    }
}
