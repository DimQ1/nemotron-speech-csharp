using System.Runtime.CompilerServices;
using SpeechLib.Llm;
using SpeechLib.Translation;

namespace SpeechLib.LiteRT.Native;

/// <summary>
/// Translates text with the on-device Gemma 4 model. A thin client of <see cref="ILlmHost"/>: every request runs at
/// <see cref="LlmPriority.Live"/>, so live translation preempts a chat answer or a background job that happens to be
/// generating and never waits behind them (about 0.3 s to the first chunk after the preemption).
/// </summary>
/// <remarks>
/// <para>Two ways to build it: with a shared <see cref="ILlmHost"/> (the app's singleton; the translator never
/// disposes it) or with <see cref="LiteRTLmNativeOptions"/> alone, which creates a private
/// <see cref="GemmaEngineHost"/> that this translator owns, loads at construction and disposes with itself.</para>
/// <para>Every sentence runs on its own conversation (the host builds it: system prompt, the previous
/// source/translation pair as a prior turn, then the sentence), so settings never leak between sentences.</para>
/// <para>Coalescing: when a newer revision of the same sentence (its text extends the older one) arrives while the
/// older request has not produced anything yet, the older request is cancelled instead of being generated for
/// nothing.</para>
/// </remarks>
public sealed class LiteRTLmNativeTranslator : ITextTranslator
{
    private readonly ILlmHost _host;
    private readonly bool _ownsHost;
    private readonly LiteRTLmNativeOptions _options;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _pendingLock = new();
    private Pending? _pending;

    /// <summary>Creates a translator with a private host (and engine) of its own; loads the model before returning.</summary>
    public LiteRTLmNativeTranslator(LiteRTLmNativeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new ArgumentException("Model path is required.", nameof(options));

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException("LiteRT-LM model file not found.", options.ModelPath);

        var host = new GemmaEngineHost(new GemmaEngineHostOptions
        {
            ModelPath = options.ModelPath,
            Backend = options.Backend,
            NumThreads = options.NumThreads,
            MaxNumTokens = options.MaxContextTokens > 0 ? options.MaxContextTokens : 4096,
            Cache = options.Cache,
            LogLevel = options.LogLevel,
            IdleUnloadAfter = null,
        });
        try
        {
            host.Preload(LlmPriority.Live);
        }
        catch
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        _host = host;
        _ownsHost = true;
    }

    /// <summary>Creates a translator over the application's shared host. The model loads at the first request (or now).</summary>
    public LiteRTLmNativeTranslator(ILlmHost host, LiteRTLmNativeOptions options)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (!host.IsModelInstalled)
            throw new FileNotFoundException("LiteRT-LM model file not found.", options.ModelPath);

        // Warm the engine at Live priority so the first sentence does not pay the load; a no-op when it is loaded.
        host.PreloadAsync(CancellationToken.None, LlmPriority.Live).GetAwaiter().GetResult();
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

        var text = new System.Text.StringBuilder();
        await foreach (var chunk in TranslateStreamAsync(request, cancellationToken).ConfigureAwait(false))
            text.Append(chunk);

        var result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            yield break;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var pending = Begin(request.Text, linked);
        try
        {
            await foreach (var chunk in _host.GenerateStreamAsync(BuildRequest(request), linked.Token).ConfigureAwait(false))
            {
                pending.Started = true;
                yield return chunk;
            }
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }
        }
    }

    private LlmRequest BuildRequest(TranslationRequest request)
    {
        // The previous sentence pair is replayed as history: the model continues as if it had just translated it,
        // which keeps the two sentences consistent.
        var messages = new List<LlmMessage>(4)
        {
            new(LlmRole.System, _options.BuildSystemPrompt(request.TargetLanguage, request.SourceLanguage)),
        };
        if (request.HasContext)
        {
            messages.Add(new LlmMessage(LlmRole.User, request.PreviousSource!));
            messages.Add(new LlmMessage(LlmRole.Assistant, request.PreviousTranslation!));
        }

        messages.Add(new LlmMessage(LlmRole.User, request.Text));

        // No explicit sampler: the CPU backend implements only top-p, so the model's own sampler applies (the host
        // would retry without one anyway).
        return new LlmRequest(
            LlmPriority.Live,
            messages,
            new LlmSampling(MaxOutputTokens: _options.MaxTokens));
    }

    /// <summary>Registers a request and cancels an older queued one that this request revises.</summary>
    private Pending Begin(string text, CancellationTokenSource cts)
    {
        var pending = new Pending(text, cts);
        Pending? superseded = null;
        lock (_pendingLock)
        {
            if (_pending is { Started: false } older && text.StartsWith(older.Text, StringComparison.Ordinal))
                superseded = older;
            _pending = pending;
        }

        try { superseded?.Cts.Cancel(); } catch (ObjectDisposedException) { }
        return pending;
    }

    public void Dispose()
    {
        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
        if (_ownsHost)
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class Pending(string text, CancellationTokenSource cts)
    {
        public string Text { get; } = text;
        public CancellationTokenSource Cts { get; } = cts;
        public volatile bool Started;
    }
}
