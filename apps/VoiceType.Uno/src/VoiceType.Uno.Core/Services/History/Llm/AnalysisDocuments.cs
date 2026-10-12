using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

/// <summary>
/// <c>analysis/vN.chunks.json</c> (design §7.4): the map step of the long-recording digest. The transcript of
/// version <c>vN</c> is cut at segment boundaries into chunks of about <see cref="ChunkTokens"/> tokens and each
/// chunk gets a short summary with <c>[mm:ss]</c> markers. Written by the <c>summarize</c> job after every chunk,
/// read by chats (<c>TranscriptContextProvider</c>) and by the job itself to resume.
/// <para>
/// A file is valid for a transcript when <see cref="Version"/>, <see cref="TextHash"/>,
/// <see cref="PromptVersion"/> and <see cref="ChunkTokens"/> all match; otherwise it is stale and is rebuilt.
/// Chunks without <see cref="AnalysisChunk.Summary"/> are not summarized yet; <see cref="Complete"/> is true
/// when all are.
/// </para>
/// </summary>
public sealed class AnalysisChunksDocument
{
    public const int CurrentSchema = 1;

    /// <summary>Bump when the summary prompt changes; older files are rebuilt.</summary>
    public const int CurrentPromptVersion = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Transcript version the chunks were made from (e.g. <c>v2</c>).</summary>
    public string Version { get; set; } = "";

    /// <summary>SHA-256 (hex, first 32 chars) of <see cref="TranscriptDocument.JoinSegments"/> of that version.</summary>
    public string TextHash { get; set; } = "";

    public int PromptVersion { get; set; } = CurrentPromptVersion;

    /// <summary>Target size of a chunk's source text in tokens (the actual size is in <see cref="AnalysisChunk.Tokens"/>).</summary>
    public int ChunkTokens { get; set; }

    /// <summary>Model that wrote the summaries.</summary>
    public string? Model { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Complete { get; set; }
    public List<AnalysisChunk> Chunks { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Whether this file describes <paramref name="textHash"/> at this prompt and chunk size.</summary>
    public bool Matches(string versionId, string textHash, int chunkTokens) =>
        Schema == CurrentSchema && PromptVersion == CurrentPromptVersion
        && Version == versionId && TextHash == textHash && ChunkTokens == chunkTokens;
}

public sealed class AnalysisChunk
{
    [JsonPropertyName("i")]
    public int Index { get; set; }

    /// <summary>First and last <see cref="TranscriptSegment.Index"/> in the chunk (inclusive).</summary>
    public int FromSegment { get; set; }
    public int ToSegment { get; set; }

    /// <summary>Start of the first and end of the last segment, in seconds of the audio.</summary>
    public double StartSec { get; set; }
    public double EndSec { get; set; }

    /// <summary>Tokens of the chunk's transcript text.</summary>
    public int Tokens { get; set; }

    /// <summary>Gemma's summary with <c>[mm:ss]</c> markers; null until summarized.</summary>
    public string? Summary { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// <c>analysis/vN.summary.json</c>: the reduce step, one summary of the whole recording (150–250 tokens) for
/// the multi-recording chat context. Same validity rule as <see cref="AnalysisChunksDocument"/>.
/// </summary>
public sealed class AnalysisSummaryDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Version { get; set; } = "";
    public string TextHash { get; set; } = "";
    public int PromptVersion { get; set; } = AnalysisChunksDocument.CurrentPromptVersion;
    public string? Model { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Number of chunk summaries it was reduced from.</summary>
    public int ChunkCount { get; set; }

    public string Text { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public bool Matches(string versionId, string textHash) =>
        Schema == CurrentSchema && PromptVersion == AnalysisChunksDocument.CurrentPromptVersion
        && Version == versionId && TextHash == textHash;
}
