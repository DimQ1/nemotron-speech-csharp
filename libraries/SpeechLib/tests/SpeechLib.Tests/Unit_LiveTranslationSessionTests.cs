using System.Runtime.CompilerServices;
using SpeechLib.Translation;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_LiveTranslationSessionTests
{
    /// <summary>Deterministic backend: upper-cases the text and records every request.</summary>
    private sealed class FakeTranslator : ITextTranslator
    {
        public List<TranslationRequest> Requests { get; } = new();
        public int DelayMs { get; set; }

        public Task<string?> TranslateAsync(string text, string targetLang, string? sourceLang = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(Render(text));

        public async IAsyncEnumerable<string> TranslateStreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            lock (Requests)
                Requests.Add(request);
            if (DelayMs > 0)
                await Task.Delay(DelayMs, cancellationToken);
            foreach (var word in Render(request.Text).Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return word + " ";
            }
        }

        private static string Render(string text) => "Translation: " + text.ToUpperInvariant();

        public void Dispose() { }
    }

    private static (LiveTranslationSession Session, FakeTranslator Translator) Create(int debounceMs = 10)
    {
        var translator = new FakeTranslator();
        var session = new LiveTranslationSession(
            _ => Task.FromResult<ITextTranslator>(translator),
            new LiveTranslationOptions { DraftDebounceMs = debounceMs })
        {
            TargetLanguage = "Russian",
        };
        return (session, translator);
    }

    [Fact]
    public async Task Feed_FinalizesCompleteSentencesAndCleansOutput()
    {
        var (session, translator) = Create();
        await using (session)
        {
            session.Feed("Hello world. Second one");
            await session.FlushAsync();

            Assert.Equal("HELLO WORLD." + Environment.NewLine + "SECOND ONE", session.DisplayText);
            Assert.Contains(translator.Requests, r => r.Text == "Hello world.");
            Assert.Contains(translator.Requests, r => r.Text == "Second one");
        }
    }

    [Fact]
    public async Task Feed_PassesPreviousSentenceAsContext()
    {
        var (session, translator) = Create();
        await using (session)
        {
            session.Feed("First one. Second one.");
            await session.FlushAsync();

            var second = translator.Requests.Single(r => r.Text == "Second one.");
            Assert.Equal("First one.", second.PreviousSource);
            Assert.Equal("FIRST ONE.", second.PreviousTranslation);
            Assert.Equal("Russian", second.TargetLanguage);
        }
    }

    [Fact]
    public async Task Feed_RevisedTailRestartsFromChangedWord()
    {
        var (session, translator) = Create();
        await using (session)
        {
            // A streaming recognizer rewrites its provisional tail; the earlier draft
            // must not leak into the final.
            session.Feed("Joe Keaton disapproved of the first");
            session.Feed("Joe Keaton disapproved of films.");
            await session.FlushAsync();

            Assert.Equal("JOE KEATON DISAPPROVED OF FILMS.", session.DisplayText);
        }
    }

    [Fact]
    public async Task Feed_RevisionInsideFinalizedSentenceDoesNotDuplicate()
    {
        var (session, _) = Create();
        await using (session)
        {
            session.Feed("Hello world. Next");
            await session.FlushAsync();

            // The recognizer "corrects" the already finalized sentence and keeps going.
            session.Feed("Hello word. Next words here.");
            await session.FlushAsync();

            var lines = session.DisplayText.Split(Environment.NewLine);
            Assert.Equal(3, lines.Length);
            Assert.Equal("HELLO WORLD.", lines[0]);
            Assert.Equal("NEXT", lines[1]);
            Assert.Equal("WORDS HERE.", lines[2]);
        }
    }

    [Fact]
    public async Task Feed_AfterFlushOnlyTranslatesNewText()
    {
        var (session, translator) = Create();
        await using (session)
        {
            session.Feed("One two.");
            await session.FlushAsync();
            session.Feed("One two. Three four.");
            await session.FlushAsync();

            Assert.Equal(1, translator.Requests.Count(r => r.Text == "One two."));
            Assert.Equal(1, translator.Requests.Count(r => r.Text == "Three four."));
        }
    }

    [Fact]
    public async Task RepeatedSentence_IsServedFromMemo()
    {
        var (session, translator) = Create();
        await using (session)
        {
            session.Feed("Same thing. Same thing.");
            await session.FlushAsync();

            Assert.Equal(1, translator.Requests.Count(r => r.Text == "Same thing."));
            Assert.Equal("SAME THING." + Environment.NewLine + "SAME THING.", session.DisplayText);
        }
    }

    [Fact]
    public async Task Reset_ClearsOutputAndContext()
    {
        var (session, translator) = Create();
        await using (session)
        {
            session.Feed("First one.");
            await session.FlushAsync();
            session.Reset();
            Assert.Equal("", session.DisplayText);

            session.Feed("Second one.");
            await session.FlushAsync();
            var second = translator.Requests.Single(r => r.Text == "Second one.");
            Assert.Null(second.PreviousSource);
        }
    }

    [Fact]
    public async Task Draft_ShowsProvisionalTextBeforeSentenceEnds()
    {
        var (session, _) = Create(debounceMs: 5);
        await using (session)
        {
            var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.TranslationChanged += text =>
            {
                if (text.Contains("HALF", StringComparison.Ordinal))
                    shown.TrySetResult(text);
            };

            session.Feed("This is half");
            var text = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("THIS IS HALF", text);
        }
    }

    [Fact]
    public async Task RevisedPreviewEndingInPeriod_IsNotFinalizedRepeatedly()
    {
        var (session, translator) = Create();
        await using (session)
        {
            // Parakeet-style previews: each revision ends with a period.
            session.Feed("Joe Keaton the first time.");
            session.Feed("Joe Keaton disapproved of film.");
            session.Feed("Joe Keaton disapproved of films. Buster");
            session.Feed("Joe Keaton disapproved of films. Buster also had");
            session.Feed("Joe Keaton disapproved of films. Buster also had reservations.");
            await session.FlushAsync();

            Assert.Equal(
                "JOE KEATON DISAPPROVED OF FILMS." + Environment.NewLine + "BUSTER ALSO HAD RESERVATIONS.",
                session.DisplayText);
            Assert.DoesNotContain(translator.Requests, r => r.Text.Contains("first time", StringComparison.Ordinal) && r.PreviousSource is not null);
        }
    }

    [Fact]
    public async Task StartFrom_SkipsExistingTranscript()
    {
        var (session, translator) = Create();
        await using (session)
        {
            session.StartFrom("Old sentence. Another old one.");
            session.Feed("Old sentence. Another old one. New words here.");
            await session.FlushAsync();

            Assert.Equal("NEW WORDS HERE.", session.DisplayText);
            Assert.DoesNotContain(translator.Requests, r => r.Text.Contains("Old", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TranslateAll_TranslatesWholeTranscriptThenFollows()
    {
        var (session, _) = Create();
        await using (session)
        {
            session.StartFrom("First one. Second one.");
            await session.TranslateAllAsync("First one. Second one.");
            Assert.Equal("FIRST ONE." + Environment.NewLine + "SECOND ONE.", session.DisplayText);

            session.Feed("First one. Second one. Third one.");
            await session.FlushAsync();
            Assert.EndsWith("THIRD ONE.", session.DisplayText);
        }
    }

    [Fact]
    public async Task ReplaceTranslator_ReloadsLazily()
    {
        var first = new FakeTranslator();
        var second = new FakeTranslator();
        var session = new LiveTranslationSession(_ => Task.FromResult<ITextTranslator>(first)) { TargetLanguage = "German" };
        await using (session)
        {
            session.Feed("One.");
            await session.FlushAsync();
            Assert.Single(first.Requests);

            await session.ReplaceTranslatorAsync(_ => Task.FromResult<ITextTranslator>(second));
            Assert.False(session.IsLoaded);

            session.Feed("One. Two.");
            await session.FlushAsync();
            Assert.Single(second.Requests);
            Assert.Equal("Two.", second.Requests[0].Text);
        }
    }
}
