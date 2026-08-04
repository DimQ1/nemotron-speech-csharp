namespace SortformerStreaming.Models;

/// <summary>
/// Mutable state for streaming Sortformer v2.1 diarization.
/// Mirrors <c>SortformerModules.SortformerStreamingState</c> from NeMo.
/// </summary>
public sealed class SortformerStreamingState
{
    /// <summary>
    /// Speaker cache embeddings.
    /// Shape: (batch_size, spkcache_len, emb_dim)
    /// </summary>
    public float[][][] Spkcache { get; set; } = Array.Empty<float[][]>();

    /// <summary>
    /// Speaker cache lengths per batch item.
    /// Shape: (batch_size,)
    /// </summary>
    public int[] SpkcacheLengths { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Speaker cache predictions corresponding to <see cref="Spkcache"/>.
    /// Shape: (batch_size, spkcache_len, n_spk)
    /// </summary>
    public float[][][] SpkcachePreds { get; set; } = Array.Empty<float[][]>();

    /// <summary>
    /// FIFO queue embeddings.
    /// Shape: (batch_size, fifo_len, emb_dim)
    /// </summary>
    public float[][][] Fifo { get; set; } = Array.Empty<float[][]>();

    /// <summary>
    /// FIFO queue lengths per batch item.
    /// Shape: (batch_size,)
    /// </summary>
    public int[] FifoLengths { get; set; } = Array.Empty<int>();

    /// <summary>
    /// FIFO queue predictions.
    /// Shape: (batch_size, fifo_len, n_spk)
    /// </summary>
    public float[][][] FifoPreds { get; set; } = Array.Empty<float[][]>();

    /// <summary>
    /// Speaker permutation applied to the previous chunk.
    /// <c>null</c> in inference mode.
    /// Shape: (batch_size, n_spk)
    /// </summary>
    public int[][]? SpkPerm { get; set; }

    /// <summary>
    /// Mean silence embedding per batch item.
    /// Shape: (batch_size, emb_dim)
    /// </summary>
    public float[][] MeanSilEmb { get; set; } = Array.Empty<float[]>();

    /// <summary>
    /// Number of accumulated silence frames per batch item.
    /// Shape: (batch_size,)
    /// </summary>
    public int[] NSilFrames { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Hyperparameters for the streaming update algorithms.
/// </summary>
public sealed class SortformerStreamingConfig
{
    public int ChunkLen { get; init; } = 188;
    public int ChunkLeftContext { get; init; } = 1;
    public int ChunkRightContext { get; init; } = 1;
    public int FifoLen { get; init; } = 0;
    public int SpkcacheLen { get; init; } = 188;
    public int SpkcacheUpdatePeriod { get; init; } = 188;
    public int SpkcacheSilFramesPerSpk { get; init; } = 3;
    public float SilThreshold { get; init; } = 0.2f;
    public float PredScoreThreshold { get; init; } = 0.25f;
    public float StrongBoostRate { get; init; } = 0.75f;
    public float WeakBoostRate { get; init; } = 1.5f;
    public float MinPosScoresRate { get; init; } = 0.5f;
    public float ScoresBoostLatest { get; init; } = 0.05f;
    public float ScoresAddRnd { get; init; } = 0f;
    public int NSpk { get; init; } = 4;
    public int MaxIndex { get; init; } = 99999;
}
