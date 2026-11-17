using System.Runtime.CompilerServices;
using System.Text;
using LiteRtLmSharp;
using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native;

/// <summary>The real engine: a thin adapter from <see cref="IGemmaEngine"/> to LiteRtLmSharp.</summary>
internal sealed class NativeGemmaEngine : IGemmaEngine
{
    private readonly LiteRtEngine _engine;

    private NativeGemmaEngine(LiteRtEngine engine, int maxNumTokens)
    {
        _engine = engine;
        MaxNumTokens = maxNumTokens;
    }

    public int MaxNumTokens { get; }

    public static NativeGemmaEngine Load(GemmaEngineHostOptions options)
    {
        LiteRtEngine.SetMinLogLevel((int)options.LogLevel);
        var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = options.ModelPath,
            Backend = LiteRtBackend.Parse(options.Backend),
            NumThreads = options.NumThreads > 0 ? options.NumThreads : null,
            MaxNumTokens = options.MaxNumTokens,
            Cache = options.Cache,
        });
        return new NativeGemmaEngine(engine, options.MaxNumTokens);
    }

    public int CountTokens(string text) => _engine.Tokenize(text).Length;

    public IGemmaConversation CreateConversation(GemmaConversationSpec spec)
    {
        var history = new List<LiteRtMessage>(spec.History.Count);
        foreach (var message in spec.History)
        {
            history.Add(message.Role == LlmRole.Assistant
                ? LiteRtMessage.Model(message.Text, toolCalls: null)
                : LiteRtMessage.User(message.Text));
        }

        var conversation = _engine.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = spec.System,
            EnableThinking = false,
            MaxOutputTokens = spec.MaxOutputTokens,
            Sampler = spec.Sampler is null
                ? null
                : new LiteRtSamplerParams
                {
                    Strategy = LiteRtSamplerType.TopP,
                    TopP = (float)spec.Sampler.TopP,
                    Temperature = (float)spec.Sampler.Temperature,
                },
            History = history.Count > 0 ? history : null,
        });
        return new NativeGemmaConversation(conversation, spec);
    }

    public IGemmaConversation Resume(IGemmaSnapshot snapshot)
    {
        if (snapshot is not GemmaConversationSnapshot saved)
            throw new ArgumentException("The snapshot is not an application text-history snapshot.", nameof(snapshot));
        return CreateConversation(saved.Spec with { History = saved.Turns });
    }

    public void Dispose() => _engine.Dispose();

    private sealed class NativeGemmaConversation(LiteRtConversation conversation, GemmaConversationSpec spec) : IGemmaConversation
    {
        private readonly GemmaConversationHistory _history = new(spec);

        public int TokenCountCached => conversation.TokenCountCached;

        public bool IsContextFull => conversation.IsContextFull;

        public IGemmaSnapshot Snapshot(string? partialResponse) => _history.Snapshot(partialResponse);

        public async IAsyncEnumerable<string> SendStreamingAsync(
            string userText,
            GemmaSendSpec send,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var options = send.NoRepeatNgram > 0
                ? new LiteRtSendOptions { NoRepeatNgram = new LiteRtNoRepeatNgramOptions { NgramSize = send.NoRepeatNgram } }
                : null;

            _history.BeginTurn(userText);
            var answer = new StringBuilder();
            await using var stream = conversation
                .SendStreamingAsync(userText, attachments: null, options, cancellationToken)
                .ConfigureAwait(false)
                .GetAsyncEnumerator();

            while (true)
            {
                bool has;
                try
                {
                    has = await stream.MoveNextAsync();
                }
                catch (LiteRtContextOverflowException ex)
                {
                    throw new LlmContextOverflowException(
                        "The conversation does not fit the model's context window: " + ex.Message,
                        ex.TokenCount, ex.MaxNumTokens, ex);
                }

                if (!has)
                {
                    _history.CompleteTurn(answer.ToString());
                    yield break;
                }

                var chunk = stream.Current;
                if (chunk.Kind == LiteRtStreamChunkKind.Answer && chunk.Text.Length > 0)
                {
                    answer.Append(chunk.Text);
                    yield return chunk.Text;
                }
            }
        }

        public void Dispose() => conversation.Dispose();
    }
}
