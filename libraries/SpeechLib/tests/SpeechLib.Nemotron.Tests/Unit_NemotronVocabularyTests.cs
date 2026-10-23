using SpeechLib.Models;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

public sealed class Unit_NemotronVocabularyTests
{
    private const int Blank = 13087;

    private static readonly Lazy<NemotronVocabulary> Shipped = new(() => NemotronVocabulary.Load(NemotronTestEnvironment.DataPath("vocab.txt"), Blank, 13088));
    private static readonly Lazy<Dictionary<string, int>> IdOf = new(() =>
    {
        var lines = NemotronTestEnvironment.ReadVocabularyLines();
        var map = new Dictionary<string, int>();
        for (var i = 0; i < lines.Length; i++)
            map[lines[i]] = i;
        return map;
    });

    private static NemotronVocabulary Vocabulary => Shipped.Value;

    private static int Id(string piece) => IdOf.Value[piece];

    [Fact]
    public void Load_ShippedVocabulary_ShouldHaveTheModelLayout()
    {
        Assert.Equal(13088, Vocabulary.Count);
        Assert.Equal(Blank, Vocabulary.BlankId);
        Assert.Equal("<unk>", Vocabulary.RawPiece(0));
        Assert.Equal("<blank>", Vocabulary.RawPiece(Blank));
        Assert.Equal("▁", Vocabulary.RawPiece(2));
        Assert.Equal(2300, Vocabulary.WordStartCount);
        Assert.Equal(39, Vocabulary.TagCount);
    }

    [Theory]
    [InlineData(1, "<bg-BG>")]
    [InlineData(256, "<cs-CZ>")]
    [InlineData(397, "<da-DK>")]
    [InlineData(518, "<de-DE>")]
    [InlineData(2021, "<ru-RU>")]
    [InlineData(2809, "<en-GB>")]
    [InlineData(2947, "<en-US>")]
    [InlineData(12944, "<vi-VN>")]
    public void LanguageTag_ShouldBeATagThatNeverFormsOrExtendsAWord(int id, string piece)
    {
        Assert.Equal(piece, Vocabulary.RawPiece(id));
        Assert.True(Vocabulary.IsTag(id));
        Assert.False(Vocabulary.StartsWord(id));
        Assert.Equal("", Vocabulary.PieceOf(id));
        Assert.Equal(piece, Vocabulary.Decode(id)); // the raw stream keeps the literal tag, like GenAI
    }

    [Fact]
    public void Tags_ShouldBeExactlyTheLocaleEntries()
    {
        var tags = Enumerable.Range(0, Vocabulary.Count).Where(Vocabulary.IsTag).Select(Vocabulary.RawPiece).ToList();

        Assert.Equal(39, tags.Count);
        Assert.All(tags, t => Assert.Matches(@"^<[a-z]{2,3}-[A-Z]{2,3}>$", t));
        Assert.DoesNotContain("<unk>", tags);
        Assert.DoesNotContain("<blank>", tags);
    }

    [Fact]
    public void SpecialIds_ShouldCarryNoWordText()
    {
        Assert.True(Vocabulary.IsSpecial(0));
        Assert.True(Vocabulary.IsSpecial(Blank));
        Assert.False(Vocabulary.IsSpecial(2));
        Assert.Equal("", Vocabulary.PieceOf(0));
        Assert.Equal("", Vocabulary.PieceOf(Blank));
        Assert.False(Vocabulary.StartsWord(0));
        Assert.False(Vocabulary.StartsWord(Blank));
    }

    [Fact]
    public void Decode_ShouldMatchGenAiTokenizerStreamForEveryId()
    {
        // Probed with GenAI 0.16.0 for all 13088 ids (fresh TokenizerStream each) and 4000 random ids in one stream:
        // the text is the piece with the word marker turned into a space; the only exception is <unk>, which GenAI
        // drops as an added special token. The blank piece decodes literally.
        for (var id = 0; id < Vocabulary.Count; id++)
        {
            var expected = id == 0 ? "" : Vocabulary.RawPiece(id).Replace('▁', ' ');
            Assert.Equal(expected, Vocabulary.Decode(id));
        }
        Assert.Equal("<blank>", Vocabulary.Decode(Blank));
    }

    [Fact]
    public void WordMarker_ShouldStartAWordAndBecomeASpace()
    {
        var the = Id("▁the");
        Assert.True(Vocabulary.StartsWord(the));
        Assert.Equal(" the", Vocabulary.PieceOf(the));
        Assert.Equal(" the", Vocabulary.Decode(the));

        Assert.True(Vocabulary.StartsWord(2));                 // the bare marker
        Assert.Equal(" ", Vocabulary.PieceOf(2));

        var continuation = Id("ing");
        Assert.False(Vocabulary.StartsWord(continuation));
        Assert.Equal("ing", Vocabulary.PieceOf(continuation));
    }

    [Fact]
    public void Detokenize_ShouldConcatenateWithTheLeadingSpaceOfTheFirstWord()
    {
        var ids = new[] { Id("▁Bu"), Id("st"), Id("er"), Id("."), 2, Id("<en-US>"), Id("▁and") };

        Assert.Equal(" Buster. <en-US> and", Vocabulary.Detokenize(ids));
        Assert.Equal("", Vocabulary.Detokenize([]));
    }

    [Fact]
    public void Detokenize_GoldenTokenStreams_ShouldGiveTheGenAiText()
    {
        var cases = GoldenCase.LoadAll().Where(c => c.Chunks.Count > 0).ToList();
        Assert.NotEmpty(cases);

        var tokens = 0;
        foreach (var golden in cases)
        {
            var ids = golden.Chunks.SelectMany(c => c.Ids).ToArray();
            tokens += ids.Length;
            Assert.Equal(golden.Text, Vocabulary.Detokenize(ids));
            Assert.Equal(golden.TokenCount, ids.Length);
        }
        Assert.True(tokens > 400, $"only {tokens} golden tokens");
    }

    [Fact]
    public void TimedWordBuilder_WithTagAfterPunctuation_ShouldNotGlueTheTagToTheWord()
    {
        // ". ▁ <ru-RU>" comes out on one frame after a sentence end, often long after the last word.
        var tokens = new[]
        {
            new TimedWordBuilder.RawToken(Id("▁the"), 0, 1280),
            new TimedWordBuilder.RawToken(Id("▁go"), 1280, 2560),
            new TimedWordBuilder.RawToken(Id("."), 2560, 3840),
            new TimedWordBuilder.RawToken(2, 3840, 12800),
            new TimedWordBuilder.RawToken(Id("<ru-RU>"), 3840, 12800),
            new TimedWordBuilder.RawToken(Id("▁on"), 12800, 14080),
        };

        var words = TimedWordBuilder.Build(tokens, 16000, Vocabulary.StartsWord, Vocabulary.PieceOf);

        Assert.Equal(["the", "go.", "on"], words.Select(w => w.Word));
        Assert.DoesNotContain(words, w => w.Word.Contains('<'));
        Assert.Equal(1280 / 16000.0, words[1].StartSeconds, 6);
        Assert.Equal(2560 / 16000.0, words[1].EndSeconds, 6); // the punctuation, the marker and the tag do not stretch the word into the pause
        Assert.Equal(12800 / 16000.0, words[2].StartSeconds, 6);
    }

    [Fact]
    public void TimedWordBuilder_WithLeadingTag_ShouldNotMakeItAWord()
    {
        var tokens = new[]
        {
            new TimedWordBuilder.RawToken(Id("<en-US>"), 0, 1280),
            new TimedWordBuilder.RawToken(Id("▁the"), 1280, 2560),
        };

        var words = TimedWordBuilder.Build(tokens, 16000, Vocabulary.StartsWord, Vocabulary.PieceOf);

        var word = Assert.Single(words);
        Assert.Equal("the", word.Word);
        Assert.Equal(1280 / 16000.0, word.StartSeconds, 6);
    }

    [Fact]
    public void FromPieces_ShouldValidateSizeAndBlank()
    {
        string[] pieces = ["<unk>", "▁a", "b", "<blank>"];

        var vocabulary = NemotronVocabulary.FromPieces(pieces, 3, expectedSize: 4);

        Assert.Equal(4, vocabulary.Count);
        Assert.Throws<InvalidDataException>(() => NemotronVocabulary.FromPieces(pieces, 3, expectedSize: 5));
        Assert.Throws<InvalidDataException>(() => NemotronVocabulary.FromPieces(pieces, 4));
        Assert.Throws<InvalidDataException>(() => NemotronVocabulary.FromPieces(pieces, -1));
        Assert.Throws<FileNotFoundException>(() => NemotronVocabulary.Load(Path.Combine(Path.GetTempPath(), "missing-vocab.txt"), 3));
    }

    [Fact]
    public void OutOfRangeIds_ShouldBeHarmless()
    {
        Assert.False(Vocabulary.StartsWord(-1));
        Assert.False(Vocabulary.StartsWord(99999));
        Assert.Equal("", Vocabulary.PieceOf(99999));
        Assert.Equal("", Vocabulary.Decode(-5));
        Assert.True(Vocabulary.IsSpecial(99999));
        Assert.False(Vocabulary.IsTag(99999));
    }
}
