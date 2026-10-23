using System.Text;
using System.Text.RegularExpressions;

namespace SpeechLib.Nemotron;

/// <summary>
/// Vocabulary and detokenizer of the Nemotron streaming model, read from <c>vocab.txt</c> (one SentencePiece
/// piece per line, token id = line index, the RNN-T blank is the last id).
/// </summary>
/// <remarks>
/// <para><see cref="Detokenize(ReadOnlySpan{int})"/> reproduces the text of GenAI's <c>TokenizerStream</c> for this
/// model: every piece with the SentencePiece word marker (U+2581) turned into a space, concatenated, so the first
/// word of a stream keeps its leading space and language tags stay literal (<c>&lt;ru-RU&gt;</c>).</para>
/// <para>Language tags (<c>&lt;en-US&gt;</c>, <c>&lt;ru-RU&gt;</c> ...) are real model output: an end-of-utterance
/// plus detected-language marker that follows the terminal punctuation, emitted on the same frame as the
/// punctuation and often long after the last word. For word building they must neither start nor extend a word,
/// which <see cref="StartsWord"/> and <see cref="PieceOf"/> guarantee (both can be handed to
/// <c>TimedWordBuilder.Build</c> as method groups).</para>
/// </remarks>
public sealed class NemotronVocabulary
{
    /// <summary>SentencePiece word-start marker, LOWER ONE EIGHTH BLOCK.</summary>
    public const char WordMarker = '▁';

    // <xx-XX> / <xxx-XXX>: the 39 locale tags of the model (<bg-BG>, <ru-RU>, <en-US>, ...).
    private static readonly Regex TagPattern = new(@"^<[a-z]{2,3}-[A-Z]{2,3}>$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string[] _pieces;      // raw vocabulary entries
    private readonly string[] _text;        // entry with the marker as a space: what GenAI decodes
    private readonly bool[] _isTag;
    private readonly bool[] _isSpecial;     // <unk> and the blank: never real text
    private readonly bool[] _decodesEmpty;  // GenAI's tokenizer drops the <unk> added token and nothing else
    private readonly bool[] _startsWord;

    private NemotronVocabulary(string[] pieces, int blankId)
    {
        _pieces = pieces;
        BlankId = blankId;
        _text = new string[pieces.Length];
        _isTag = new bool[pieces.Length];
        _isSpecial = new bool[pieces.Length];
        _decodesEmpty = new bool[pieces.Length];
        _startsWord = new bool[pieces.Length];

        for (var id = 0; id < pieces.Length; id++)
        {
            var piece = pieces[id];
            _text[id] = piece.Replace(WordMarker, ' ');
            _isTag[id] = TagPattern.IsMatch(piece);
            _isSpecial[id] = id == blankId || piece == "<unk>" || piece == "<blank>";
            _decodesEmpty[id] = piece == "<unk>";
            _startsWord[id] = !_isTag[id] && !_isSpecial[id] && piece.Length > 0 && piece[0] == WordMarker;
            if (_startsWord[id])
                WordStartCount++;
            if (_isTag[id])
                TagCount++;
        }
    }

    /// <summary>Number of ids (13088 for the shipped model).</summary>
    public int Count => _pieces.Length;

    /// <summary>Id of the RNN-T blank (the last id).</summary>
    public int BlankId { get; }

    /// <summary>Number of pieces that begin with the word marker.</summary>
    public int WordStartCount { get; }

    /// <summary>Number of language tag pieces.</summary>
    public int TagCount { get; }

    /// <summary>Reads <c>vocab.txt</c>.</summary>
    /// <exception cref="InvalidDataException">The file does not have exactly <paramref name="expectedSize"/> entries (when given) or the blank id is outside it.</exception>
    public static NemotronVocabulary Load(string vocabPath, int blankId, int? expectedSize = null)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Nemotron vocabulary not found: {vocabPath}", vocabPath);
        return FromPieces(File.ReadAllLines(vocabPath, new UTF8Encoding(false)), blankId, expectedSize);
    }

    /// <summary>Builds a vocabulary from its pieces (id = index).</summary>
    public static NemotronVocabulary FromPieces(IReadOnlyList<string> pieces, int blankId, int? expectedSize = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (expectedSize is { } size && pieces.Count != size)
            throw new InvalidDataException($"vocab.txt has {pieces.Count} entries, the model config says {size}.");
        if (blankId < 0 || blankId >= pieces.Count)
            throw new InvalidDataException($"Blank id {blankId} is outside the vocabulary of {pieces.Count} entries.");
        return new NemotronVocabulary(pieces.ToArray(), blankId);
    }

    /// <summary>The raw vocabulary entry (with the U+2581 marker).</summary>
    public string RawPiece(int id) => _pieces[id];

    /// <summary>True for a language tag such as <c>&lt;en-US&gt;</c>.</summary>
    public bool IsTag(int id) => IsKnown(id) && _isTag[id];

    /// <summary>True for ids that never carry text: <c>&lt;unk&gt;</c> and the blank.</summary>
    public bool IsSpecial(int id) => !IsKnown(id) || _isSpecial[id];

    /// <summary>True when the token begins a word: a regular piece with the word marker. Tags never do.</summary>
    public bool StartsWord(int id) => IsKnown(id) && _startsWord[id];

    /// <summary>
    /// The piece for word building: the text of the token with the marker as a space, or empty for language
    /// tags and special ids so that they never form or extend a word.
    /// </summary>
    public string PieceOf(int id) => IsKnown(id) && !_isTag[id] && !_isSpecial[id] ? _text[id] : "";

    /// <summary>
    /// The text GenAI's <c>TokenizerStream</c> decodes for one token, verified for every id of the shipped
    /// vocabulary: tags and the blank piece are literal, only the <c>&lt;unk&gt;</c> added token decodes to nothing.
    /// </summary>
    public string Decode(int id) => IsKnown(id) && !_decodesEmpty[id] ? _text[id] : "";

    /// <summary>Decodes a token sequence into the stream text (concatenation of <see cref="Decode"/>).</summary>
    public string Detokenize(ReadOnlySpan<int> ids)
    {
        if (ids.IsEmpty)
            return "";
        var text = new StringBuilder();
        foreach (var id in ids)
            text.Append(Decode(id));
        return text.ToString();
    }

    /// <summary>Appends the decoded text of <paramref name="id"/> to <paramref name="builder"/>.</summary>
    public void AppendDecoded(StringBuilder builder, int id)
    {
        if (IsKnown(id) && !_decodesEmpty[id])
            builder.Append(_text[id]);
    }

    private bool IsKnown(int id) => (uint)id < (uint)_pieces.Length;
}
