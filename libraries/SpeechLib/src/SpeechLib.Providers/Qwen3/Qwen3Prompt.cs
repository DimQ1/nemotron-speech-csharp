namespace SpeechLib.Qwen3;

/// <summary>
/// Prompt template and special token IDs for Qwen3-ASR, plus the encoder
/// output length formula. Ported from the exporter reference
/// (qwen3-asr-onnx/src/prompt.py, encoder_wrapper.py).
/// </summary>
internal static class Qwen3Prompt
{
    public const int EndOfTextTokenId = 151643;  // <|endoftext|> (EOS)
    public const int ImStartTokenId = 151644;    // <|im_start|>
    public const int ImEndTokenId = 151645;      // <|im_end|> (EOS)
    public const int AudioStartTokenId = 151669; // <|audio_start|>
    public const int AudioEndTokenId = 151670;   // <|audio_end|>
    public const int AudioPadTokenId = 151676;   // <|audio_pad|> (replaced by encoder output)
    public const int AsrTextTokenId = 151704;    // <asr_text>

    public const int ConvWindow = 100;
    public const int TokensPerWindow = 13;

    /// <summary>True for both EOS tokens (<|endoftext|> and <|im_end|>).</summary>
    public static bool IsEos(int token) => token is EndOfTextTokenId or ImEndTokenId;

    /// <summary>
    /// Number of encoder tokens produced from a mel frame count.
    /// Matches the native Qwen3-ASR formula with ONNX-safe integer division.
    /// </summary>
    public static int GetEncoderOutputLength(int melFrames)
    {
        int leave = melFrames % ConvWindow;
        int t = ConvOutLen(leave);
        t = ConvOutLen(t);
        t = ConvOutLen(t);
        return t + melFrames / ConvWindow * TokensPerWindow;
    }

    private static int ConvOutLen(int t) => (t + 1) / 2;

    /// <summary>
    /// Builds the prompt token sequence for ASR transcription:
    /// <|im_start|>system\n<|im_end|>\n<|im_start|>user\n<|audio_start|>[pad x N]<|audio_end|><|im_end|>\n<|im_start|>assistant\n
    /// Returns the ids and the index where <|audio_pad|> tokens begin (the
    /// decoder_init audio_offset).
    /// </summary>
    public static long[] BuildPromptIds(int audioTokenCount, out int audioOffset)
    {
        const int newline = 198;
        var ids = new List<long>(audioTokenCount + 16)
        {
            ImStartTokenId, 9125, newline, ImEndTokenId, newline,       // system turn
            ImStartTokenId, 882, newline, AudioStartTokenId,            // user turn
        };
        audioOffset = ids.Count;
        for (int i = 0; i < audioTokenCount; i++)
            ids.Add(AudioPadTokenId);
        ids.Add(AudioEndTokenId);
        ids.Add(ImEndTokenId);
        ids.Add(newline);
        ids.Add(ImStartTokenId);                                        // assistant turn
        ids.Add(77091);
        ids.Add(newline);
        return ids.ToArray();
    }
}
