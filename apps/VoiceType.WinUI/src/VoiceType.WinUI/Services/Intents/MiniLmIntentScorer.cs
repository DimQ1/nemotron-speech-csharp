using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace VoiceType.WinUI.Services.Intents;

public sealed class MiniLmIntentScorer : IIntentScorer
{
    private const int MaxTokens = 128;
    private const int HiddenSize = 384;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly Dictionary<string, float[]> _exemplarEmbeddings = new();
    private readonly IntentVectorStore? _vectorStore;
    private bool _disposed;

    public string ModelId { get; }

    public MiniLmIntentScorer(
        string modelDir,
        VoiceIntentTaxonomy taxonomy,
        string modelId = "paraphrase-multilingual-MiniLM-L12-v2-int8",
        string? vectorStorePath = null)
    {
        ModelId = modelId;

        var onnxPath = Path.Combine(modelDir, "onnx", "model_int8.onnx");
        var tokenizerPath = Path.Combine(modelDir, "sentencepiece.bpe.model");
        _session = new InferenceSession(onnxPath);

        using var stream = File.OpenRead(tokenizerPath);
        _tokenizer = SentencePieceTokenizer.Create(
            stream,
            addBeginningOfSentence: true,
            addEndOfSentence: true);

        try
        {
            var file = new FileInfo(onnxPath);
            var modelKey = $"{ModelId}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            _vectorStore = new IntentVectorStore(vectorStorePath ?? AppPaths.VoiceIntentVectorsFile);
            ModelId = modelKey;
        }
        catch
        {
            // SQLite caching is an optimization; semantic routing remains available.
        }

        CacheExemplars(taxonomy);
    }

    public Task<VoiceIntentScores> ScoreAsync(string text, CancellationToken ct)
    {
        var embedding = Embed(text);
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _exemplarEmbeddings)
        {
            ct.ThrowIfCancellationRequested();
            var intentId = pair.Key.Split("::", 2)[0];
            var score = CosineSimilarity(embedding, pair.Value);
            if (!scores.TryGetValue(intentId, out var current) || score > current)
                scores[intentId] = score;
        }

        var top = scores.OrderByDescending(pair => pair.Value).FirstOrDefault();
        return Task.FromResult(new VoiceIntentScores(scores, top.Key ?? "", top.Value));
    }

    private void CacheExemplars(VoiceIntentTaxonomy taxonomy)
    {
        foreach (var intent in taxonomy.Intents)
        {
            foreach (var exemplar in intent.Exemplars)
            {
                _exemplarEmbeddings[$"{intent.Id}::{exemplar}"] = _vectorStore is null
                    ? Embed(exemplar)
                    : _vectorStore.GetOrAdd(ModelId, intent.Id, exemplar, () => Embed(exemplar));
            }
        }
    }

    private float[] Embed(string text)
    {
        var encoded = _tokenizer.EncodeToIds(
            text,
            addBeginningOfSentence: true,
            addEndOfSentence: true);
        var ids = encoded.Take(MaxTokens).ToArray();
        var length = ids.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, MaxTokens });
        var attentionMask = new DenseTensor<long>(new[] { 1, MaxTokens });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, MaxTokens });
        for (var index = 0; index < length; index++)
        {
            inputIds[0, index] = ids[index];
            attentionMask[0, index] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = _session.Run(inputs);
        var hidden = results.First(result => result.Name == "last_hidden_state").AsTensor<float>();
        var pooled = new float[HiddenSize];

        for (var token = 0; token < length; token++)
        {
            for (var hiddenIndex = 0; hiddenIndex < HiddenSize; hiddenIndex++)
                pooled[hiddenIndex] += hidden[0, token, hiddenIndex];
        }

        for (var hiddenIndex = 0; hiddenIndex < HiddenSize; hiddenIndex++)
            pooled[hiddenIndex] /= Math.Max(length, 1);

        var norm = MathF.Sqrt(pooled.Sum(value => value * value));
        if (norm > 1e-8f)
        {
            for (var hiddenIndex = 0; hiddenIndex < HiddenSize; hiddenIndex++)
                pooled[hiddenIndex] /= norm;
        }

        return pooled;
    }

    private static float CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length || left.Length == 0)
            return 0f;

        float dot = 0;
        float leftNorm = 0;
        float rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        var denominator = MathF.Sqrt(leftNorm) * MathF.Sqrt(rightNorm);
        return denominator < 1e-8f ? 0f : dot / denominator;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _session.Dispose();
    }
}