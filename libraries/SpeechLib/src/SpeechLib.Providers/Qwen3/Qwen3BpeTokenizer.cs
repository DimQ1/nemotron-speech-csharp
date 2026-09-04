using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpeechLib.Qwen3;

/// <summary>
/// Byte-level BPE tokenizer compatible with the Qwen (GPT-2 style) tokenizer.
/// Loads vocab.json (token -> id) and merges.txt (merge priority) from the
/// model folder and decodes generated token ids back to text.
/// </summary>
internal sealed class Qwen3BpeTokenizer
{
    // GPT-2 pre-tokenizer pattern (as used by Qwen tokenizers).
    private static readonly Regex Pattern = new(
        @"'(?i:[sdmt]|ll|ve|re)|[^\r\n\p{L}\p{N}]?+\p{L}++|\p{N}{1,3}+| ?[^\s\p{L}\p{N}]++[\r\n]*+|\s++$|\s*[\r\n]|\s+(?!\S)|\s",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    private readonly Dictionary<string, int> _tokenToId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _idToToken = new();
    private readonly Dictionary<(string, string), int> _mergeRanks = new();

    private static readonly byte[] ByteToUtf8 = new byte[256];
    private static readonly Dictionary<byte, char> ByteToChar = new();

    static Qwen3BpeTokenizer()
    {
        // GPT-2 byte->unicode map: printable bytes map to themselves; the rest
        // map to 256+n.
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            bool printable = b is >= 33 and <= 126 or >= 161 and <= 172 or >= 174;
            char c = printable ? (char)b : (char)(256 + n++);
            ByteToChar[(byte)b] = c;
            ByteToUtf8[c] = (byte)b;
        }
    }

    private Qwen3BpeTokenizer() { }

    public static Qwen3BpeTokenizer Load(string modelDir)
    {
        var t = new Qwen3BpeTokenizer();

        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "vocab.json"))))
        {
            foreach (var kv in doc.RootElement.EnumerateObject())
            {
                int id = kv.Value.GetInt32();
                t._tokenToId[kv.Name] = id;
                t._idToToken[id] = kv.Name;
            }
        }

        // Added (special) tokens with exact ids, from tokenizer_config.json.
        var tcfg = Path.Combine(modelDir, "tokenizer_config.json");
        if (File.Exists(tcfg))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(tcfg));
            if (doc.RootElement.TryGetProperty("added_tokens_decoder", out var added))
            {
                foreach (var kv in added.EnumerateObject())
                {
                    int id = int.Parse(kv.Name);
                    string content = kv.Value.GetProperty("content").GetString()!;
                    t._idToToken[id] = content;
                    t._tokenToId[content] = id;
                }
            }
        }

        int rank = 0;
        // merges.txt is only needed for encoding (prompt building uses fixed
        // token ids, so the inference path only decodes). The ONNX export
        // folder does not ship merges.txt; load it when present.
        var mergesPath = Path.Combine(modelDir, "merges.txt");
        if (File.Exists(mergesPath))
        {
            foreach (var line in File.ReadLines(mergesPath))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                t._mergeRanks[(line[..sp], line[(sp + 1)..])] = rank++;
            }
        }

        return t;
    }

    /// <summary>Decodes token ids to text, skipping special tokens.</summary>
    public string Decode(IEnumerable<long> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (!_idToToken.TryGetValue((int)id, out var token)) continue;
            if (token.StartsWith('<') && token.EndsWith('>')) continue; // special tokens
            foreach (char c in token)
                bytes.Add(ByteToUtf8[c]);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Encodes text to token ids (used for building prompts).</summary>
    public List<long> Encode(string text)
    {
        var ids = new List<long>();
        foreach (Match m in Pattern.Matches(text))
        {
            // Map each byte of the UTF-8 text to its GPT-2 char.
            var sb = new StringBuilder(m.Value.Length);
            foreach (byte b in Encoding.UTF8.GetBytes(m.Value))
                sb.Append(ByteToChar[b]);
            foreach (var tok in Bpe(sb.ToString()))
                if (_tokenToId.TryGetValue(tok, out int id))
                    ids.Add(id);
        }
        return ids;
    }

    private IEnumerable<string> Bpe(string token)
    {
        var word = new List<string>(token.Select(c => c.ToString()));
        if (word.Count <= 1) return word;

        while (true)
        {
            int bestRank = int.MaxValue, best = -1;
            for (int i = 0; i < word.Count - 1; i++)
            {
                if (_mergeRanks.TryGetValue((word[i], word[i + 1]), out int rank) && rank < bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }
            if (best < 0) break;
            word[best] = word[best] + word[best + 1];
            word.RemoveAt(best + 1);
        }
        return word;
    }
}
