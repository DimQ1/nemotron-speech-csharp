using System.Text;
using SpeechLib.Llm;
using SpeechLib.Translation;

namespace VoiceType.Uno.History;

public sealed class SavedTranslationOptions
{
    /// <summary>Upper bound of the prompt tokens of one unit (engine-improvements §2: prefill cannot be cancelled).</summary>
    public int MaxUnitPromptTokens { get; init; } = 800;

    public int MaxSentenceTokens { get; init; } = TranslationPlanner.DefaultMaxSentenceTokens;

    /// <summary>Tries per sentence when the host fails for a reason that is not a cancellation of the job.</summary>
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Low temperature: translation should be repeatable. Null members keep the model's sampler.</summary>
    public LlmSampling Sampling { get; init; } = new(Temperature: 0.1, TopP: 0.5);
}

/// <summary>Progress after a saved unit.</summary>
public sealed record SavedTranslationProgress(int SentencesDone, int SentencesTotal, int UnitsDone, int UnitsTotal)
{
    public bool IsComplete => SentencesDone >= SentencesTotal;
    public double Fraction => SentencesTotal == 0 ? 1 : (double)SentencesDone / SentencesTotal;
}

/// <summary>
/// Translates a saved transcript on the on-device LLM (design §8), at <see cref="LlmPriority.Background"/>.
/// <list type="bullet">
/// <item>The segments are cut into sentences (<see cref="TranslationPlanner"/>); each sentence is one
/// request with the previous source/translation pair as context, the same prompt, output cleaning and
/// guards as the live translation (<see cref="TranslationPrompt"/>, <see cref="TranslationOutputCleaner"/>,
/// <see cref="TranslationGuards"/>).</item>
/// <item>Sentences are grouped into units of ≈ 500–800 prompt tokens. After each unit
/// <c>unitSaved</c> receives the document so far (the caller saves it as a partial translation and
/// offers a checkpoint). A request is idempotent: when the host preempts it for live translation it
/// re-runs it; when it fails it is retried; neither costs more than one sentence.</item>
/// <item>Translations keep the segment timing: each sentence's text goes to the segment it starts in,
/// so the document has one entry per source segment (segments that only continue a sentence are empty).</item>
/// <item>Cancellation stops within a decode step; the sentences finished in the current unit are
/// delivered through <c>unitSaved</c> before <see cref="OperationCanceledException"/> propagates.</item>
/// </list>
/// </summary>
public sealed class SavedTranscriptTranslator
{
    private readonly ILlmHost _host;
    private readonly SavedTranslationOptions _options;

    public SavedTranscriptTranslator(ILlmHost host, SavedTranslationOptions? options = null)
    {
        _host = host;
        _options = options ?? new SavedTranslationOptions();
    }

    /// <param name="segments">Segments of the source version.</param>
    /// <param name="targetLanguage">Code or name ("ru", "Russian").</param>
    /// <param name="sourceLanguage">Source language code or name; null lets the model detect it.</param>
    /// <param name="resume">The partial translation of the same source version and language, or null to start over.</param>
    /// <param name="unitSaved">Called after every unit (and with the finished part of a cancelled unit).</param>
    public async Task<TranslationDocument> TranslateAsync(
        IReadOnlyList<TranscriptSegment> segments,
        string targetLanguage,
        string? sourceLanguage,
        TranslationDocument? resume,
        Action<TranslationDocument, SavedTranslationProgress>? unitSaved,
        CancellationToken cancellationToken)
    {
        var target = TranslationLanguages.NameFor(targetLanguage)
            ?? throw new ArgumentException("A translation needs a target language.", nameof(targetLanguage));
        var source = TranslationLanguages.NameFor(sourceLanguage);

        var plan = TranslationPlanner.Split(segments, _host.CountTokens, _options.MaxSentenceTokens);
        var translations = new List<string>(plan.Count);
        if (resume is not null && IsResumable(resume, plan))
            translations.AddRange(resume.Sentences!.Select(s => s.Text));

        var systemTokens = _host.CountTokens(TranslationPrompt.BuildSystemPrompt(target, source));
        var units = TranslationPlanner.PlanUnits(plan, translations.Count, systemTokens, _options.MaxUnitPromptTokens);
        var allUnits = translations.Count + units.Count; // only used for the "unit n of m" progress
        var unitsDone = translations.Count == 0 ? 0 : allUnits - units.Count;
        var savedCount = translations.Count;

        TranslationDocument Build() => BuildDocument(segments, plan, translations, target);

        void Save()
        {
            if (translations.Count == savedCount)
                return;
            savedCount = translations.Count;
            unitSaved?.Invoke(Build(), new SavedTranslationProgress(translations.Count, plan.Count, unitsDone, allUnits));
        }

        if (plan.Count == 0)
        {
            var empty = Build();
            unitSaved?.Invoke(empty, new SavedTranslationProgress(0, 0, 0, 0));
            return empty;
        }

        try
        {
            foreach (var unit in units)
            {
                for (var i = unit.FirstSentence; i < unit.FirstSentence + unit.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var previous = i > 0 ? plan[i - 1] : null;
                    var previousTranslation = i > 0 ? translations[i - 1] : null;
                    translations.Add(await TranslateSentenceAsync(plan[i], previous, previousTranslation, target, source, cancellationToken).ConfigureAwait(false));
                }
                unitsDone++;
                Save();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Save(); // sentences finished inside the interrupted unit are kept
            throw;
        }
        catch
        {
            Save();
            throw;
        }

        return Build();
    }

    /// <summary>Whether <paramref name="resume"/> is the beginning of the translation of exactly this plan.</summary>
    public static bool IsResumable(TranslationDocument resume, IReadOnlyList<PlannedSentence> plan)
    {
        var done = resume.Sentences;
        if (done is null || done.Count != resume.SentencesDone || resume.SentencesTotal != plan.Count || done.Count > plan.Count)
            return false;
        for (var i = 0; i < done.Count; i++)
        {
            if (done[i].Segment != plan[i].StartSegment)
                return false;
        }
        return true;
    }

    private async Task<string> TranslateSentenceAsync(
        PlannedSentence sentence, PlannedSentence? previous, string? previousTranslation,
        string target, string? source, CancellationToken cancellationToken)
    {
        // Already in the target language (Cyrillic text to "Russian"): Gemma would only echo and rewrite it.
        if (TranslationGuards.IsAlreadyInTarget(sentence.Text, target))
            return sentence.Text;

        var withContext = new TranslationRequest(sentence.Text, target)
        {
            SourceLanguage = source,
            PreviousSource = previous?.Text,
            PreviousTranslation = previousTranslation,
        };
        var output = await RunAsync(withContext, sentence, cancellationToken).ConfigureAwait(false);
        if (IsUsable(output, sentence) || !withContext.HasContext)
            return Finish(output, sentence);

        // A runaway or empty answer with context: once more without it (the context may have confused the model).
        var alone = withContext with { PreviousSource = null, PreviousTranslation = null };
        return Finish(await RunAsync(alone, sentence, cancellationToken).ConfigureAwait(false), sentence);
    }

    private static bool IsUsable(string output, PlannedSentence sentence) =>
        output.Length > 0 && !TranslationGuards.IsRunaway(output, sentence.Text);

    /// <summary>The cleaned answer, or the source sentence itself when the model gave nothing usable (nothing is lost).</summary>
    private static string Finish(string output, PlannedSentence sentence) =>
        IsUsable(output, sentence) ? output : sentence.Text;

    private async Task<string> RunAsync(TranslationRequest request, PlannedSentence sentence, CancellationToken cancellationToken)
    {
        var messages = new List<LlmMessage> { new(LlmRole.System, TranslationPrompt.BuildSystemPrompt(request.TargetLanguage, request.SourceLanguage)) };
        if (request.HasContext)
        {
            messages.Add(new LlmMessage(LlmRole.User, request.PreviousSource!));
            messages.Add(new LlmMessage(LlmRole.Assistant, request.PreviousTranslation!));
        }
        messages.Add(new LlmMessage(LlmRole.User, request.Text));

        var sampling = _options.Sampling with { MaxOutputTokens = Math.Clamp(sentence.Tokens * 3 + 32, 64, 1024) };
        var llm = new LlmRequest(LlmPriority.Background, messages, sampling);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var raw = await _host.GenerateAsync(llm, cancellationToken).ConfigureAwait(false);
                var cleaned = TranslationOutputCleaner.Clean(raw);
                return TranslationGuards.StripContextEcho(cleaned, request, final: true).Trim();
            }
            catch (LlmUnavailableException)
            {
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && attempt < _options.MaxAttempts)
            {
                // The host failed or cancelled this request on its own (preempted by Live and not re-run,
                // engine hiccup): the request is idempotent, so just ask again.
                if (_options.RetryDelay > TimeSpan.Zero)
                    await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The translation file for the first <c>translations.Count</c> sentences: one entry per source
    /// segment up to the first untranslated sentence, each holding the sentences that start in it.
    /// </summary>
    public static TranslationDocument BuildDocument(
        IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<PlannedSentence> plan,
        IReadOnlyList<string> translations, string targetLanguage)
    {
        var done = translations.Count;
        var bySegment = new Dictionary<int, StringBuilder>();
        var sentences = new List<TranslationSentence>(done);
        for (var i = 0; i < done; i++)
        {
            var segment = plan[i].StartSegment;
            sentences.Add(new TranslationSentence { Segment = segment, Text = translations[i] });
            if (!bySegment.TryGetValue(segment, out var text))
                bySegment[segment] = text = new StringBuilder();
            if (text.Length > 0)
                text.Append(' ');
            text.Append(translations[i]);
        }

        var frontier = done >= plan.Count ? int.MaxValue : plan[done].StartSegment;
        var entries = new List<TranslationSegment>();
        foreach (var segment in segments)
        {
            var has = bySegment.TryGetValue(segment.Index, out var text);
            if (segment.Index < frontier || has)
                entries.Add(new TranslationSegment { Index = segment.Index, Text = has ? text!.ToString() : "" });
        }

        return new TranslationDocument
        {
            TargetLanguage = targetLanguage,
            Segments = entries,
            Sentences = sentences,
            SentencesDone = done,
            SentencesTotal = plan.Count,
        };
    }
}
