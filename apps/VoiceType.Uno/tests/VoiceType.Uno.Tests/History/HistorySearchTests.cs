using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistorySearchTests
{
    [Theory]
    [InlineData("Ёлка", "елка")]
    [InlineData("ЁЖИК В ТУМАНЕ", "ежик в тумане")]
    [InlineData("Café Déjà vu", "cafe deja vu")]
    [InlineData("Zürich naïve", "zurich naive")]
    [InlineData("Йошкар-Ола, май", "йошкар-ола, май")]
    [InlineData("", "")]
    public void Normalize_ShouldFoldCaseAndStripDiacritics(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Theory]
    [InlineData("Okay,", "okay")]
    [InlineData("—", "")]
    [InlineData("let's", "lets")]
    [InlineData("«Ёж»", "еж")]
    public void NormalizeToken_ShouldDropPunctuation(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.NormalizeToken(input));

    private static HistorySearchDocument Doc(
        string id, int day, string transcript, string? title = null, string model = "parakeet",
        string language = "ru", bool audio = true, string? translation = null, string? chat = null)
    {
        var manifest = new RecordingManifest
        {
            Id = id,
            Title = title,
            StartedAt = new DateTimeOffset(2026, 10, day, 10, 0, 0, TimeSpan.Zero),
            Audio = audio ? new AudioInfo { File = "audio.ogg" } : null,
            CurrentVersion = "v1",
            Versions =
            [
                new TranscriptVersion
                {
                    Id = "v1", Excerpt = transcript,
                    Model = new ModelInfo { Folder = model, CatalogRepoId = "org/" + model },
                    Language = new LanguageInfo { Requested = "auto", Detected = language },
                },
            ],
        };
        var translations = new List<TranslationDocument>();
        if (translation is not null)
        {
            manifest.Translations.Add(new TranslationInfo { Id = "t1", SourceVersion = "v1", TargetLanguage = "en" });
            translations.Add(new TranslationDocument { Segments = [new TranslationSegment { Text = translation }] });
        }
        var chats = chat is null
            ? []
            : new[] { new ChatRecord { Title = "c", Messages = [new ChatMessage { Text = chat }] } };
        return HistorySearch.CreateDocument(manifest, HistoryFixtures.Transcript((0, 5, "Intro."), (5, 9, transcript)), translations, chats);
    }

    [Fact]
    public void Search_ShouldMatchRussianYoAndCaseInsensitively()
    {
        var docs = new[] { Doc("a", 1, "Купили ёлку на рынке", title: "Market"), Doc("b", 2, "Ничего общего") };

        var hits = HistorySearch.Search(docs, "ЕЛКУ");

        var hit = Assert.Single(hits);
        Assert.Equal("a", hit.Document.RecordingId);
        Assert.Equal(SearchField.Transcript, hit.MatchedIn);
        Assert.Equal(5, hit.Segment!.Start);
    }

    [Fact]
    public void Search_ShouldMatchDiacriticsBothWays()
    {
        var docs = new[] { Doc("a", 1, "Meeting at the café") };

        Assert.Single(HistorySearch.Search(docs, "cafe"));
        Assert.Single(HistorySearch.Search(docs, "CAFÉ"));
    }

    [Fact]
    public void Search_ShouldRequireAllTermsAcrossFields()
    {
        var docs = new[]
        {
            Doc("a", 1, "budget review", title: "Weekly sync", translation: "обзор бюджета", chat: "deadline friday"),
            Doc("b", 2, "budget only"),
        };

        var hit = Assert.Single(HistorySearch.Search(docs, "weekly бюджета friday budget"));

        Assert.Equal("a", hit.Document.RecordingId);
        Assert.Equal(SearchField.Title | SearchField.Transcript | SearchField.Translation | SearchField.Chat, hit.MatchedIn);
    }

    [Fact]
    public void Search_ShouldUseFallbackTitleFromFirstWords()
    {
        var docs = new[] { Doc("a", 1, "alpha beta") };

        Assert.Equal("alpha beta", docs[0].Title);
        Assert.Equal(SearchField.Title | SearchField.Transcript, HistorySearch.Search(docs, "alpha").Single().MatchedIn);
    }

    [Fact]
    public void Search_ShouldOrderRecentFirstAndReturnAllForEmptyQuery()
    {
        var docs = new[] { Doc("old", 1, "x"), Doc("new", 3, "x"), Doc("mid", 2, "x") };

        Assert.Equal(["new", "mid", "old"], HistorySearch.Search(docs, "  ").Select(h => h.Document.RecordingId));
    }

    [Fact]
    public void Search_ShouldApplyFilters()
    {
        var docs = new[]
        {
            Doc("a", 1, "x", model: "parakeet", language: "ru", audio: true, translation: "t"),
            Doc("b", 2, "x", model: "qwen3", language: "en", audio: false, chat: "c"),
            Doc("c", 3, "x", model: "parakeet", language: "en", audio: true),
        };

        IEnumerable<string> Ids(HistoryFilter f) => HistorySearch.Search(docs, null, f).Select(h => h.Document.RecordingId);

        Assert.Equal(["c", "a"], Ids(new HistoryFilter { Model = "parakeet" }));
        Assert.Equal(["c", "a"], Ids(new HistoryFilter { Model = "org/parakeet" }));
        Assert.Equal(["c", "b"], Ids(new HistoryFilter { Language = "EN" }));
        Assert.Equal(["a"], Ids(new HistoryFilter { HasTranslation = true }));
        Assert.Equal(["b"], Ids(new HistoryFilter { HasChat = true }));
        Assert.Equal(["b"], Ids(new HistoryFilter { HasAudio = false }));
        Assert.Equal(["b"], Ids(new HistoryFilter
        {
            From = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
        }));
    }

    [Fact]
    public void BuildIndex_ShouldLoadTranscriptsTranslationsAndChatsFromStores()
    {
        using var dir = new TempDirectory();
        var store = new RecordingStore(Path.Combine(dir.Path, "sessions"));
        var chats = new ChatStore(Path.Combine(dir.Path, "chats"));
        var entry = store.CreateRecording(HistoryFixtures.T0);
        store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript((0, 2, "Перенесли встречу на понедельник")));
        store.SaveTranslation(entry.Id, new TranslationInfo { SourceVersion = "v1", TargetLanguage = "en" },
            new TranslationDocument { Segments = [new TranslationSegment { Text = "Moved the meeting to Monday" }] });
        chats.Create("q", entry.Id, "v1");
        chats.AppendMessage(chats.LoadAll().Chats[0].Id, new ChatMessage { Text = "what about the budget?" });

        var index = HistorySearch.BuildIndex(store, chats);

        Assert.Single(HistorySearch.Search(index, "понедельник monday budget"));
        Assert.True(index[0].HasChat);
        Assert.True(index[0].HasTranslation);
    }
}
