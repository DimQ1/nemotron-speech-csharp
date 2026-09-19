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

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "Chinese", ["zh-cn"] = "Chinese", ["zh-tw"] = "Chinese", ["4"] = "Chinese", ["5"] = "Chinese",
        ["yue"] = "Cantonese", ["zh-hk"] = "Cantonese",
        ["uk"] = "Ukrainian",
        ["en"] = "English", ["en-us"] = "English", ["en-gb"] = "English", ["0"] = "English", ["1"] = "English",
        ["ar"] = "Arabic", ["ar-ar"] = "Arabic", ["7"] = "Arabic",
        ["de"] = "German", ["de-de"] = "German", ["9"] = "German",
        ["fr"] = "French", ["fr-fr"] = "French", ["8"] = "French",
        ["es"] = "Spanish", ["es-es"] = "Spanish", ["es-us"] = "Spanish", ["2"] = "Spanish", ["3"] = "Spanish",
        ["pt"] = "Portuguese", ["pt-br"] = "Portuguese", ["pt-pt"] = "Portuguese", ["12"] = "Portuguese", ["13"] = "Portuguese",
        ["id"] = "Indonesian",
        ["it"] = "Italian", ["it-it"] = "Italian", ["15"] = "Italian",
        ["ko"] = "Korean", ["ko-kr"] = "Korean", ["14"] = "Korean",
        ["ru"] = "Russian", ["ru-ru"] = "Russian", ["11"] = "Russian",
        ["th"] = "Thai", ["vi"] = "Vietnamese",
        ["ja"] = "Japanese", ["ja-jp"] = "Japanese", ["10"] = "Japanese",
        ["tr"] = "Turkish", ["tr-tr"] = "Turkish", ["18"] = "Turkish",
        ["hi"] = "Hindi", ["hi-in"] = "Hindi", ["6"] = "Hindi",
        ["ms"] = "Malay",
        ["nl"] = "Dutch", ["nl-nl"] = "Dutch", ["16"] = "Dutch",
        ["sv"] = "Swedish", ["sv-se"] = "Swedish", ["24"] = "Swedish",
        ["da"] = "Danish", ["da-dk"] = "Danish", ["25"] = "Danish",
        ["fi"] = "Finnish", ["fi-fi"] = "Finnish", ["26"] = "Finnish",
        ["pl"] = "Polish", ["pl-pl"] = "Polish", ["17"] = "Polish",
        ["cs"] = "Czech", ["cs-cz"] = "Czech", ["22"] = "Czech",
        ["fil"] = "Filipino", ["tl"] = "Filipino",
        ["fa"] = "Persian",
        ["el"] = "Greek", ["el-gr"] = "Greek", ["21"] = "Greek",
        ["ro"] = "Romanian", ["ro-ro"] = "Romanian", ["20"] = "Romanian",
        ["hu"] = "Hungarian", ["hu-hu"] = "Hungarian", ["23"] = "Hungarian",
        ["mk"] = "Macedonian",
    };

    private static readonly string[] CanonicalLanguageNames =
        LanguageNames.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>True for both EOS tokens (<|endoftext|> and <|im_end|>).</summary>
    public static bool IsEos(int token) => token is EndOfTextTokenId or ImEndTokenId;

    /// <summary>Maps a BCP-47 code or supported language id to Qwen's canonical name.</summary>
    public static bool TryNormalizeLanguage(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var key = value.Trim();
        if (key.Equals("auto", StringComparison.OrdinalIgnoreCase) || key == "101")
            return true;

        if (LanguageNames.TryGetValue(key, out var name))
        {
            normalized = name;
            return true;
        }

        return false;
    }

    /// <summary>Extracts a Qwen language marker from the decoder prefix.</summary>
    public static bool TryDetectLanguage(string? text, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in CanonicalLanguageNames)
        {
            if (words.Any(word => word.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                normalized = name;
                return true;
            }
        }

        return false;
    }

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
    public static long[] BuildPromptIds(int audioTokenCount, out int audioOffset) =>
        BuildPromptIds(audioTokenCount, null, null, out audioOffset);

    public static long[] BuildPromptIds(
        int audioTokenCount,
        IReadOnlyList<long>? systemPromptIds,
        IReadOnlyList<long>? languagePromptIds,
        out int audioOffset)
    {
        const int newline = 198;
        var ids = new List<long>(audioTokenCount + 16
            + (systemPromptIds?.Count ?? 0)
            + (languagePromptIds?.Count ?? 0))
        {
            ImStartTokenId, 9125, newline,                              // system turn
        };
        if (systemPromptIds is not null)
            ids.AddRange(systemPromptIds);
        ids.Add(ImEndTokenId);
        ids.Add(newline);
        ids.AddRange([ImStartTokenId, 882, newline, AudioStartTokenId]); // user turn
        audioOffset = ids.Count;
        for (int i = 0; i < audioTokenCount; i++)
            ids.Add(AudioPadTokenId);
        ids.Add(AudioEndTokenId);
        ids.Add(ImEndTokenId);
        ids.Add(newline);
        ids.Add(ImStartTokenId);                                        // assistant turn
        ids.Add(77091);
        ids.Add(newline);
        if (languagePromptIds is not null)
            ids.AddRange(languagePromptIds);
        return ids.ToArray();
    }
}
