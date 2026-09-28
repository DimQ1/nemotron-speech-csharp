using System.Runtime.CompilerServices;
using LiteRtLmSharp;
using SpeechLib.Translation;

namespace SpeechLib.LiteRT.Native;

/// <summary>
/// Translates text using a Gemma 4 model in <c>.litertlm</c> format loaded
/// in-process through LiteRT-LM (via LiteRtLmSharp) — no HTTP server required.
/// </summary>
/// <remarks>
/// <para>The engine is created once and reused; every translation runs on its own
/// conversation so configuration never leaks between sentences. When the request
/// carries the previous sentence and its translation, they are replayed as a prior
/// user/assistant turn so the model keeps pronouns, terminology and register
/// consistent across sentences.</para>
/// <para>Inference is serialized through an internal semaphore because CPU decode is
/// the bottleneck and a single engine should not interleave decode passes.</para>
/// </remarks>
public sealed class LiteRTLmNativeTranslator : ITextTranslator
{
    private readonly LiteRTLmNativeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LiteRtEngine _engine;
    private readonly LiteRtSendOptions? _sendOptions;

    // The native CPU engine has been seen to refuse an explicit sampler under memory
    // pressure ("Sampler type: 3 not implemented yet"). When a send fails before the
    // first token, it is retried once with the model's own sampler and the explicit
    // one stays off for the rest of this engine's life.
    private volatile bool _samplerDisabled;

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

        _sendOptions = options.NoRepeatNgramSize > 0
            ? new LiteRtSendOptions { NoRepeatNgram = new LiteRtNoRepeatNgramOptions { NgramSize = options.NoRepeatNgramSize } }
            : null;
    }

    /// <inheritdoc />
    public Task<string?> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken cancellationToken = default) =>
        TranslateAsync(new TranslationRequest(text, targetLang) { SourceLanguage = sourceLang }, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> TranslateStreamAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken cancellationToken = default) =>
        TranslateStreamAsync(new TranslationRequest(text, targetLang) { SourceLanguage = sourceLang }, cancellationToken);

    /// <inheritdoc />
    public async Task<string?> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiteRtResponse response;
            try
            {
                using var conversation = CreateConversation(request);
                response = await conversation
                    .SendAsync(request.Text, attachments: null, _sendOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LiteRtException) when (DisableSamplerForRetry())
            {
                using var conversation = CreateConversation(request);
                response = await conversation
                    .SendAsync(request.Text, attachments: null, _sendOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = response.Text?.Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Exception filter: turns the explicit sampler off once and allows one retry.</summary>
    private bool DisableSamplerForRetry()
    {
        if (_samplerDisabled || !_options.Greedy)
            return false;
        _samplerDisabled = true;
        return true;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            yield break;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var conversation = CreateConversation(request);
            var stream = conversation
                .SendStreamingAsync(request.Text, attachments: null, _sendOptions, cancellationToken)
                .ConfigureAwait(false)
                .GetAsyncEnumerator();
            LiteRtConversation? retryConversation = null;
            try
            {
                // The first chunk is where a native send failure surfaces; nothing has been
                // yielded yet, so it is safe to start over without the explicit sampler.
                bool hasFirst;
                try
                {
                    hasFirst = await stream.MoveNextAsync();
                }
                catch (LiteRtException) when (DisableSamplerForRetry())
                {
                    await stream.DisposeAsync();
                    retryConversation = CreateConversation(request);
                    stream = retryConversation
                        .SendStreamingAsync(request.Text, attachments: null, _sendOptions, cancellationToken)
                        .ConfigureAwait(false)
                        .GetAsyncEnumerator();
                    hasFirst = await stream.MoveNextAsync();
                }

                while (hasFirst)
                {
                    var chunk = stream.Current;
                    if (chunk.Kind == LiteRtStreamChunkKind.Answer && chunk.Text.Length > 0)
                        yield return chunk.Text;
                    hasFirst = await stream.MoveNextAsync();
                }
            }
            finally
            {
                await stream.DisposeAsync();
                retryConversation?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private LiteRtConversation CreateConversation(TranslationRequest request)
    {
        // The previous sentence pair is replayed as history: the model continues as if
        // it had just translated it, which keeps the two sentences consistent.
        IReadOnlyList<LiteRtMessage>? history = request.HasContext
            ?
            [
                LiteRtMessage.User(request.PreviousSource!),
                LiteRtMessage.Model(request.PreviousTranslation!, toolCalls: null),
            ]
            : null;

        return _engine.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = _options.BuildSystemPrompt(request.TargetLanguage, request.SourceLanguage),
            EnableThinking = false,
            MaxOutputTokens = _options.MaxTokens,
            Sampler = _options.Greedy && !_samplerDisabled
                ? new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy }
                : null,
            History = history,
        });
    }

    public void Dispose()
    {
        _engine.Dispose();
        _gate.Dispose();
    }
}
