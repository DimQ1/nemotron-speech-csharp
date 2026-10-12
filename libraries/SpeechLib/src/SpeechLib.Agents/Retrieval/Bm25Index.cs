using System.Globalization;
using System.Text;

namespace SpeechLib.Agents;

/// <summary>
/// Lower-cases, folds ё to е, splits on non letters/digits, drops stop words and applies a light
/// suffix stemmer for English and Russian (enough for "meetings" ~ "meeting", "поставки" ~ "поставка").
/// </summary>
public static class TextTerms
{
    private static readonly HashSet<string> s_stop = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "of", "to", "in", "on", "at", "for", "with", "by", "is", "are", "was", "were",
        "be", "been", "it", "its", "this", "that", "these", "those", "as", "from", "about", "what", "which", "who", "whom",
        "how", "when", "where", "why", "do", "does", "did", "i", "you", "we", "they", "he", "she", "me", "my", "our", "your",
        "there", "any", "all", "can", "could", "would", "should", "will", "has", "have", "had", "not", "no", "so", "if", "then",
        "и", "в", "во", "не", "что", "он", "на", "я", "с", "со", "как", "а", "то", "все", "она", "так", "его", "но", "да",
        "ты", "к", "у", "же", "вы", "за", "бы", "по", "только", "ее", "мне", "было", "вот", "от", "меня", "еще", "нет", "о",
        "из", "ему", "теперь", "когда", "даже", "ну", "ли", "если", "уже", "или", "ни", "быть", "был", "него", "до", "вас",
        "нибудь", "опять", "уж", "вам", "ведь", "там", "потом", "себя", "ничего", "ей", "может", "они", "тут", "где", "есть",
        "надо", "ней", "для", "мы", "тебя", "их", "чем", "была", "сам", "чтоб", "без", "будто", "чего", "раз", "тоже", "себе",
        "под", "будет", "ж", "тогда", "кто", "этот", "того", "потому", "этого", "какой", "совсем", "ним", "здесь", "этом",
        "один", "почти", "мой", "тем", "чтобы", "нее", "кажется", "сейчас", "были", "куда", "зачем", "всех", "никогда",
        "можно", "при", "наконец", "два", "об", "другой", "хоть", "после", "над", "больше", "тот", "через", "эти", "нас",
        "про", "всего", "них", "какая", "много", "разве", "три", "эту", "моя", "впрочем", "хорошо", "свою", "этой", "перед",
    };

    private static readonly string[] s_ruSuffixes =
    [
        "ившись", "ывшись", "ование", "ования", "ыми", "ими", "ами", "ями", "ого", "его", "ому", "ему", "ать", "ять", "ить",
        "еть", "ует", "уют", "ает", "яет", "ают", "яют", "ился", "ался", "ов", "ев", "ей", "ам", "ям", "ах", "ях", "ом", "ем",
        "ий", "ый", "ой", "ая", "яя", "ое", "ее", "ые", "ие", "ую", "юю", "ет", "ут", "ют", "ит", "ат", "ят", "ла", "ли",
        "ло", "ся", "сь", "ть", "ны", "на", "но", "а", "я", "о", "е", "ы", "и", "у", "ю", "ь", "й",
    ];

    private static readonly string[] s_enSuffixes = ["ations", "ation", "ingly", "ings", "ing", "edly", "ies", "ied", "ed", "es", "ly", "s"];

    public static List<string> Tokenize(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;

        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0)
                return;
            var word = sb.ToString();
            sb.Clear();
            if (word.Length < 2 && !char.IsDigit(word[0]))
                return;
            if (s_stop.Contains(word))
                return;
            result.Add(Stem(word));
        }

        foreach (var raw in text)
        {
            var c = char.ToLowerInvariant(raw);
            if (c == 'ё')
                c = 'е';
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else
                Flush();
        }
        Flush();
        return result;
    }

    internal static string Stem(string word)
    {
        if (word.Length <= 3 || char.IsDigit(word[0]))
            return word;

        if (IsCyrillic(word[0]))
        {
            foreach (var suffix in s_ruSuffixes)
            {
                if (word.Length - suffix.Length >= 3 && word.EndsWith(suffix, StringComparison.Ordinal))
                {
                    word = word[..^suffix.Length];
                    break;
                }
            }
            return word.Length > 7 ? word[..7] : word;
        }

        foreach (var suffix in s_enSuffixes)
        {
            if (word.Length - suffix.Length >= 3 && word.EndsWith(suffix, StringComparison.Ordinal))
                return word[..^suffix.Length];
        }
        return word;
    }

    private static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

    /// <summary>Plain-text key used for caching: lower-cased, ё folded, whitespace collapsed.</summary>
    public static string Normalize(string text) =>
        string.Join(' ', text.ToLower(CultureInfo.InvariantCulture).Replace('ё', 'е')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Okapi BM25 over a fixed set of documents (chunks of all sources of one chat).</summary>
public sealed class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly Dictionary<string, int>[] _termFreq;
    private readonly int[] _length;
    private readonly Dictionary<string, int> _docFreq = new(StringComparer.Ordinal);
    private readonly double _avgLength;

    public Bm25Index(IReadOnlyList<string> documents)
    {
        Count = documents.Count;
        _termFreq = new Dictionary<string, int>[Count];
        _length = new int[Count];
        long total = 0;
        for (var i = 0; i < Count; i++)
        {
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            var terms = TextTerms.Tokenize(documents[i]);
            foreach (var term in terms)
                tf[term] = tf.GetValueOrDefault(term) + 1;
            _termFreq[i] = tf;
            _length[i] = terms.Count;
            total += terms.Count;
            foreach (var term in tf.Keys)
                _docFreq[term] = _docFreq.GetValueOrDefault(term) + 1;
        }
        _avgLength = Count == 0 ? 0 : Math.Max(1.0, (double)total / Count);
    }

    public int Count { get; }

    /// <summary>BM25 score of every document for <paramref name="query"/> (0 when no term matches).</summary>
    public double[] Score(string query)
    {
        var scores = new double[Count];
        var queryTerms = TextTerms.Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        foreach (var term in queryTerms)
        {
            if (!_docFreq.TryGetValue(term, out var n))
                continue;
            var idf = Math.Log(1.0 + (Count - n + 0.5) / (n + 0.5));
            for (var i = 0; i < Count; i++)
            {
                if (!_termFreq[i].TryGetValue(term, out var f))
                    continue;
                var norm = f + K1 * (1 - B + B * _length[i] / _avgLength);
                scores[i] += idf * f * (K1 + 1) / norm;
            }
        }
        return scores;
    }

    /// <summary>Indexes of the best documents, best first; documents with a zero score are not returned.</summary>
    public IReadOnlyList<int> Search(string query, int limit)
    {
        var scores = Score(query);
        return Enumerable.Range(0, Count)
            .Where(i => scores[i] > 0)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => i)
            .Take(limit)
            .ToList();
    }
}
