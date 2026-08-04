using LVA.Core.Context;
using LVA.Core.Intents;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace LVA.Nlu.Intents;

/// <summary>Which tokenizer format the embedding model uses.</summary>
public enum EmbeddingTokenizerKind
{
    /// <summary>WordPiece (BERT-style vocab.txt).</summary>
    Bert,
    /// <summary>SentencePiece unigram (XLM-RoBERTa-style tokenizer.json).</summary>
    SentencePiece
}

/// <summary>
/// L1 embedding scorer: paraphrase-multilingual-MiniLM-L12-v2 INT8.
/// Pipeline: BertTokenizer → ONNX (input_ids/attention_mask/token_type_ids)
/// → last_hidden_state → attention-masked mean-pooling → L2 normalize → cosine
/// against cached intent exemplar embeddings (TZ §4.3).
/// </summary>
public sealed class MiniLmEmbeddingModel : IIntentModel
{
    private const int MaxTokens = 128;
    private const int HiddenSize = 384;

    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
    private readonly EmbeddingTokenizerKind _tokenizerKind;
    private readonly Dictionary<string, float[]> _exemplarEmbeddings = new();
    private readonly string _modelId;
    private bool _disposed;

    public string ModelId => _modelId;

    public MiniLmEmbeddingModel(string modelId, string modelPath, string tokenizerPath, IntentTaxonomy taxonomy,
        EmbeddingTokenizerKind tokenizerKind = EmbeddingTokenizerKind.SentencePiece)
    {
        _modelId = modelId;
        _tokenizerKind = tokenizerKind;
        _session = new InferenceSession(modelPath);
        _tokenizer = tokenizerKind == EmbeddingTokenizerKind.Bert
            ? BertTokenizer.Create(tokenizerPath)
            : CreateSentencePiece(tokenizerPath);
        CacheExemplars(taxonomy);
    }

    private static SentencePieceTokenizer CreateSentencePiece(string modelPath)
    {
        using var stream = File.OpenRead(modelPath);
        // XLM-RoBERTa convention: emit <s> and </s> special tokens.
        return SentencePieceTokenizer.Create(stream, addBeginningOfSentence: true, addEndOfSentence: true);
    }

    private void CacheExemplars(IntentTaxonomy taxonomy)
    {
        foreach (var intent in taxonomy.Intents)
        {
            foreach (var exemplar in intent.Exemplars)
            {
                // Store per-exemplar; scoring takes max cosine across exemplars of an intent.
                _exemplarEmbeddings[$"{intent.Id}::{exemplar}"] = Embed(exemplar);
            }
        }
    }

    public Task<IntentScores> ScoreAsync(IntentInput input, CancellationToken ct)
    {
        var emb = Embed(input.Text);
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, exemplarEmb) in _exemplarEmbeddings)
        {
            var intentId = key.Split("::", 2)[0];
            var cos = ContextBuffer.CosineSimilarity(emb, exemplarEmb);
            if (!scores.TryGetValue(intentId, out var current) || cos > current)
                scores[intentId] = cos;
        }

        var top = scores.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return Task.FromResult(new IntentScores(scores, top.Key ?? "", top.Value));
    }

    private float[] Embed(string text)
    {
        IReadOnlyList<int> encoded = _tokenizerKind == EmbeddingTokenizerKind.Bert
            ? ((BertTokenizer)_tokenizer).EncodeToIds(text, addSpecialTokens: true)
            : ((SentencePieceTokenizer)_tokenizer).EncodeToIds(text, addBeginningOfSentence: true, addEndOfSentence: true);
        var ids = encoded.Take(MaxTokens).ToArray();
        int len = ids.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, MaxTokens });
        var attention = new DenseTensor<long>(new[] { 1, MaxTokens });
        var tokenTypes = new DenseTensor<long>(new[] { 1, MaxTokens });
        for (int i = 0; i < len; i++)
        {
            inputIds[0, i] = ids[i];
            attention[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes)
        };

        using var results = _session.Run(inputs);
        var hidden = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();

        // Attention-masked mean pooling.
        var pooled = new float[HiddenSize];
        for (int t = 0; t < len; t++)
            for (int h = 0; h < HiddenSize; h++)
                pooled[h] += hidden[0, t, h];
        for (int h = 0; h < HiddenSize; h++)
            pooled[h] /= Math.Max(len, 1);

        // L2 normalize.
        var norm = MathF.Sqrt(pooled.Sum(v => v * v));
        if (norm > 1e-8f)
            for (int h = 0; h < HiddenSize; h++)
                pooled[h] /= norm;
        return pooled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
