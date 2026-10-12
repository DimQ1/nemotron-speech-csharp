using System.Diagnostics;
using System.Text;
using SpeechLib.Llm;
using SpeechLib.Translation;
using Xunit.Abstractions;

namespace SpeechLib.LiteRT.Native.Tests;

/// <summary>Runs only when SPEECHLIB_GEMMA_MODEL points at gemma-4-E2B-it.litertlm (it loads 3+ GB and takes minutes).</summary>
public sealed class RealModelFactAttribute : FactAttribute
{
    public const string EnvVar = "SPEECHLIB_GEMMA_MODEL";

    public RealModelFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Skip = $"Set {EnvVar} to the path of gemma-4-E2B-it.litertlm to run the real-model checks.";
    }
}

/// <summary>
/// Real-model measurements of <see cref="GemmaEngineHost"/> (docs/research/litert/engine-improvements.md §2). Each test
/// prints its numbers; run one at a time with
/// <c>SPEECHLIB_GEMMA_MODEL=... dotnet test --filter "FullyQualifiedName~Real_" -c Release</c>.
/// </summary>
public sealed class Real_GemmaEngineHostTests(ITestOutputHelper output)
{
    private static string ModelPath => Environment.GetEnvironmentVariable(RealModelFactAttribute.EnvVar)!;

    private static GemmaEngineHost NewHost(TimeSpan? idleUnload = null) => new(new GemmaEngineHostOptions
    {
        ModelPath = ModelPath,
        IdleUnloadAfter = idleUnload,
        LogLevel = LiteRTLmLogLevel.Error,
    });

    private void Log(string line)
    {
        output.WriteLine(line);
        var report = Environment.GetEnvironmentVariable("SPEECHLIB_GEMMA_REPORT");
        if (!string.IsNullOrEmpty(report))
            File.AppendAllText(report, line + Environment.NewLine);
    }

    private static double Mb() => Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;

    private static LlmRequest Translate(string sentence, string? prev = null, string? prevTranslation = null)
    {
        var system = TranslationPrompt.BuildSystemPrompt("Russian", "English", "");
        var messages = new List<LlmMessage> { new(LlmRole.System, system) };
        if (prev is not null)
        {
            messages.Add(new LlmMessage(LlmRole.User, prev));
            messages.Add(new LlmMessage(LlmRole.Assistant, prevTranslation!));
        }

        messages.Add(new LlmMessage(LlmRole.User, sentence));
        return new LlmRequest(LlmPriority.Live, messages, new LlmSampling(MaxOutputTokens: 160));
    }

    private const string Sentence = "The weather is really nice today, so we decided to go for a long walk in the park near the river.";

    private static string Paragraphs(int count)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            sb.Append($"Section {i}. The harbour town of Port {i} was founded in {1500 + i * 7} by merchants from the north. ");
            sb.Append("Its council met every spring to decide how the tolls on salt, timber and wool should be shared among the guilds, ");
            sb.Append($"and the minutes record that guild number {i} always asked for a lower toll on timber while the fishermen asked for a lighter tax on nets. ");
            sb.Append("The harbour master kept a ledger of every ship, its cargo, its captain and the date it left, and these ledgers are the best surviving source on trade in the region. ");
        }

        return sb.ToString();
    }

    private async Task<(double FirstChunkMs, double TotalMs, string Text)> TimedAsync(
        ILlmHost host, LlmRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        double first = -1;
        var text = new StringBuilder();
        await foreach (var chunk in host.GenerateStreamAsync(request, ct))
        {
            if (first < 0)
                first = sw.Elapsed.TotalMilliseconds;
            text.Append(chunk);
        }

        return (first, sw.Elapsed.TotalMilliseconds, text.ToString());
    }

    [RealModelFact]
    public async Task Real_A_LiveTranslationAloneVersusDuringChatGeneration()
    {
        await using var host = NewHost();
        await host.PreloadAsync();
        Log($"[A] engine loaded in {host.LastLoadDuration!.Value.TotalMilliseconds:N0} ms, private {Mb():N0} MB");

        // Warm-up (first send pays a one-time cost) and baseline.
        await TimedAsync(host, Translate(Sentence));
        var alone = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var r = await TimedAsync(host, Translate(Sentence));
            alone.Add(r.FirstChunkMs);
            Log($"[A] live alone #{i + 1}: first chunk {r.FirstChunkMs:N0} ms, total {r.TotalMs:N0} ms -> {r.Text.Trim()}");
        }

        var shortPrompt = new LlmRequest(LlmPriority.Live,
            [new(LlmRole.System, "Translate to Russian."), new(LlmRole.User, Sentence)], new LlmSampling(MaxOutputTokens: 160));
        var shortResult = await TimedAsync(host, shortPrompt);
        Log($"[A] live alone with a minimal system prompt: first chunk {shortResult.FirstChunkMs:N0} ms, total {shortResult.TotalMs:N0} ms; system prompt of the app = {host.CountTokens(TranslationPrompt.BuildSystemPrompt("Russian", "English", ""))} tokens");

        var switchTimes = new List<double>();
        var preempted = new List<double>();
        for (var run = 1; run <= 3; run++)
        {
            var chat = new LlmRequest(LlmPriority.Interactive,
            [
                new(LlmRole.System, "You are a helpful assistant."),
                new(LlmRole.User, "Write a detailed essay of about 500 words on the history of the Roman Republic."),
            ], new LlmSampling(MaxOutputTokens: 400));

            var chatText = new StringBuilder();
            var chatSw = Stopwatch.StartNew();
            var marker = -1;
            var chatTask = Task.Run(async () =>
            {
                await foreach (var chunk in host.GenerateStreamAsync(chat))
                    lock (chatText)
                        chatText.Append(chunk);
            });

            // Let the chat reach steady decode (prefill is short for this prompt), then ask for a translation.
            await Task.Delay(2500);
            int before;
            lock (chatText)
            {
                before = chatText.Length;
                marker = before;
            }

            var enqueuedAt = Stopwatch.GetTimestamp();
            double switchMs = -1;
            void OnStatus(LlmHostStatus st)
            {
                if (switchMs < 0 && st.RunningPriority == LlmPriority.Live)
                    switchMs = Stopwatch.GetElapsedTime(enqueuedAt).TotalMilliseconds;
            }

            host.StatusChanged += OnStatus;
            var live = await TimedAsync(host, Translate(Sentence));
            host.StatusChanged -= OnStatus;
            switchTimes.Add(switchMs);
            Log($"[A] #{run}: chat cancelled and live started {switchMs:N0} ms after the request was queued");
            preempted.Add(live.FirstChunkMs);
            Log($"[A] live during chat #{run}: chat had {before} chars; live first chunk {live.FirstChunkMs:N0} ms, total {live.TotalMs:N0} ms -> {live.Text.Trim()}");

            await chatTask;
            var full = chatText.ToString();
            Log($"[A] chat #{run} finished in {chatSw.Elapsed.TotalSeconds:N1} s, {full.Length} chars; status {host.Status.State}");
            if (run == 1)
            {
                var join = Math.Min(marker, full.Length);
                Log("[A] --- chat text around the preemption point (marker |<>|) ---");
                Log(full[..join] + "|<>|" + full[join..]);
                Log("[A] --- end ---");
            }
        }

        Log($"[A] summary: live alone first chunk avg {alone.Average():N0} ms; live during chat (preempted) first chunk avg {preempted.Average():N0} ms; cancel-to-live-start avg {switchTimes.Average():N0} ms");
        Assert.True(preempted.Average() < 2500, "live translation must not wait for the whole chat answer");
    }

    [RealModelFact]
    public async Task Real_G_LiveTranslationSessionOverSharedHostWhileChatRuns()
    {
        await using var host = NewHost();
        await host.PreloadAsync();
        var options = new LiteRTLmNativeOptions { MaxTokens = 160 };
        using var translator = new LiteRTLmNativeTranslator(host, options);
        await using var session = new LiveTranslationSession(_ => Task.FromResult<ITextTranslator>(translator))
        {
            TargetLanguage = "Russian",
        };

        var chat = new LlmRequest(LlmPriority.Interactive,
            [new(LlmRole.System, "You are a helpful assistant."), new(LlmRole.User, "Write about 400 words about the history of tea.")],
            new LlmSampling(MaxOutputTokens: 400));
        var chatTask = TimedAsync(host, chat);
        await Task.Delay(2500);

        var sw = Stopwatch.StartNew();
        string? shown = null;
        session.TranslationChanged += t => shown = t;
        session.Feed("Good morning everyone. Today we will talk about the new project plan. ");
        await session.FlushAsync();
        Log($"[G] session translated 2 sentences during a chat generation in {sw.Elapsed.TotalSeconds:N1} s: {shown}");
        var chatResult = await chatTask;
        Log($"[G] chat finished after {chatResult.TotalMs / 1000:N1} s, {chatResult.Text.Length} chars");
        Assert.Matches("[а-яА-Я]", shown ?? "");
    }

    [RealModelFact]
    public async Task Real_F_ContinuationPromptVariants()
    {
        var variants = new[]
        {
            new GemmaEngineHostOptions { ModelPath = ModelPath }.ContinuationPrompt,
            "Continue exactly where you stopped. Start with the very next word. Do not use an ellipsis and do not repeat or summarize anything you already wrote.",
            "Your previous reply was cut off in the middle of a sentence. Output only the remaining text, beginning with the word that comes right after the last word you wrote.",
        };

        for (var v = 0; v < variants.Length; v++)
        {
            await using var host = new GemmaEngineHost(new GemmaEngineHostOptions
            {
                ModelPath = ModelPath,
                LogLevel = LiteRTLmLogLevel.Error,
                ContinuationPrompt = variants[v],
            });
            await host.PreloadAsync();
            var chat = new LlmRequest(LlmPriority.Interactive,
            [
                new(LlmRole.System, "You are a helpful assistant."),
                new(LlmRole.User, "Explain in about 250 words how a refrigerator works."),
            ], new LlmSampling(MaxOutputTokens: 300));

            var text = new StringBuilder();
            var consumer = Task.Run(async () =>
            {
                await foreach (var c in host.GenerateStreamAsync(chat))
                    lock (text)
                        text.Append(c);
            });
            await Task.Delay(4000);
            int marker;
            lock (text)
                marker = text.Length;
            await host.GenerateAsync(Translate(Sentence));
            await consumer;
            var full = text.ToString();
            Log($"[F] variant {v + 1} (preempted after {marker} chars): " + full[..marker] + " |<>| " + full[marker..]);
            Log("[F] ----");
        }
    }

    [RealModelFact]
    public async Task Real_B_ChatFollowUpStatefulVersusReplay()
    {
        await using var host = NewHost();
        await host.PreloadAsync();

        var context = Paragraphs(9);
        Log($"[B] context ~{host.CountTokens(context)} tokens");
        var system = new LlmMessage(LlmRole.System, "You answer questions about the document the user pasted. Be brief.");

        foreach (var round in new[] { 1, 2 })
        {
            var key = "chat-" + round;
            var q1 = new LlmMessage(LlmRole.User, context + "\n\nQuestion: Which year was Port 3 founded?");
            var first = await TimedAsync(host, new LlmRequest(LlmPriority.Interactive, [system, q1], new LlmSampling(MaxOutputTokens: 60), key));
            Log($"[B] round {round} turn 1 (cold, builds conversation): first chunk {first.FirstChunkMs:N0} ms, total {first.TotalMs:N0} ms -> {first.Text.Trim()}");

            var history = new List<LlmMessage> { system, q1, new(LlmRole.Assistant, first.Text) };
            var q2 = new LlmMessage(LlmRole.User, "And which guild asked for a lower toll on timber in Port 5?");

            var stateful = await TimedAsync(host, new LlmRequest(LlmPriority.Interactive, [.. history, q2], new LlmSampling(MaxOutputTokens: 60), key));
            Log($"[B] round {round} follow-up STATEFUL: first chunk {stateful.FirstChunkMs:N0} ms, total {stateful.TotalMs:N0} ms -> {stateful.Text.Trim()}");

            var replay = await TimedAsync(host, new LlmRequest(LlmPriority.Interactive, [.. history, q2], new LlmSampling(MaxOutputTokens: 60), "replay-" + round));
            Log($"[B] round {round} follow-up REPLAY (new key): first chunk {replay.FirstChunkMs:N0} ms, total {replay.TotalMs:N0} ms -> {replay.Text.Trim()}");

            host.ReleaseConversation(key);
            host.ReleaseConversation("replay-" + round);
        }
    }

    [RealModelFact]
    public async Task Real_C_BackgroundUnitPreemptedByLiveAndRerun()
    {
        await using var host = NewHost();
        await host.PreloadAsync();
        await TimedAsync(host, Translate(Sentence));

        var unit = Paragraphs(4);
        var system = TranslationPrompt.BuildSystemPrompt("Russian", "English", "");
        var request = new LlmRequest(LlmPriority.Background,
            [new(LlmRole.System, system), new(LlmRole.User, unit)], new LlmSampling(MaxOutputTokens: 300));
        Log($"[C] background unit ~{host.CountTokens(unit)} tokens");

        var clean = await TimedAsync(host, request);
        Log($"[C] background alone: total {clean.TotalMs:N0} ms, {clean.Text.Length} chars");

        var statuses = new List<string>();
        host.StatusChanged += s =>
        {
            lock (statuses)
                statuses.Add($"{DateTime.Now:ss.fff} {s.State} run={s.RunningPriority} q={s.QueuedCount} {s.Detail}");
        };

        var sw = Stopwatch.StartNew();
        var bg = TimedAsync(host, request);
        await Task.Delay(3000); // in decode by now
        var liveSw = Stopwatch.StartNew();
        var live = await TimedAsync(host, Translate(Sentence));
        Log($"[C] live arrived at +3000 ms during background decode: first chunk {live.FirstChunkMs:N0} ms, total {live.TotalMs:N0} ms -> {live.Text.Trim()}");
        var rerun = await bg;
        Log($"[C] background finished at +{sw.Elapsed.TotalMilliseconds:N0} ms (clean run {clean.TotalMs:N0} ms): {rerun.Text.Length} chars, delivered whole, first chunk {rerun.FirstChunkMs:N0} ms");
        lock (statuses)
            Log("[C] statuses: " + string.Join(" | ", statuses));
        Assert.NotEmpty(rerun.Text);
    }

    [RealModelFact]
    public async Task Real_D_MixedSoak100Requests()
    {
        await using var host = NewHost();
        await host.PreloadAsync();
        await TimedAsync(host, Translate(Sentence));
        GC.Collect();
        var baseline = Mb();
        Log($"[D] baseline private {baseline:N0} MB");

        var random = new Random(11);
        var histories = new Dictionary<string, List<LlmMessage>>();
        var latencies = new Dictionary<LlmPriority, List<double>>
        {
            [LlmPriority.Live] = [], [LlmPriority.Interactive] = [], [LlmPriority.Background] = [],
        };
        var failures = 0;
        var system = new LlmMessage(LlmRole.System, "Answer in one short sentence.");

        async Task OneAsync(int i)
        {
            var roll = random.Next(10);
            var sw = Stopwatch.StartNew();
            try
            {
                if (roll < 4)
                {
                    var r = Translate($"Number {i}: the meeting starts at {random.Next(1, 12)} o'clock tomorrow morning.");
                    await host.GenerateAsync(r with { Sampling = new LlmSampling(MaxOutputTokens: 40) });
                    latencies[LlmPriority.Live].Add(sw.Elapsed.TotalMilliseconds);
                }
                else if (roll < 7)
                {
                    var key = "k" + random.Next(4);
                    List<LlmMessage> history;
                    lock (histories)
                        history = histories.TryGetValue(key, out var h) ? [.. h] : [system];
                    var user = new LlmMessage(LlmRole.User, $"Tell me one fact about the number {i}.");
                    var answer = await host.GenerateAsync(new LlmRequest(LlmPriority.Interactive, [.. history, user], new LlmSampling(MaxOutputTokens: 30), key));
                    history.Add(user);
                    history.Add(new LlmMessage(LlmRole.Assistant, answer));
                    if (history.Count > 9)
                        history = [system, .. history.Skip(history.Count - 4)];
                    lock (histories)
                        histories[key] = history;
                    latencies[LlmPriority.Interactive].Add(sw.Elapsed.TotalMilliseconds);
                }
                else
                {
                    await host.GenerateAsync(new LlmRequest(LlmPriority.Background,
                        [new(LlmRole.System, "Summarize in five words."), new(LlmRole.User, Paragraphs(1))], new LlmSampling(MaxOutputTokens: 20)));
                    latencies[LlmPriority.Background].Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log($"[D] request {i} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var total = Stopwatch.StartNew();
        for (var batch = 0; batch < 20; batch++)
        {
            // Five overlapping requests per batch, so the queue and preemption are exercised.
            var tasks = Enumerable.Range(batch * 5, 5).Select(i => Task.Run(async () =>
            {
                await Task.Delay(random.Next(0, 400));
                await OneAsync(i);
            })).ToArray();
            await Task.WhenAll(tasks);
            if ((batch + 1) % 5 == 0)
            {
                GC.Collect();
                Log($"[D] after {(batch + 1) * 5} requests: private {Mb():N0} MB, live conversations {host.LiveConversationCount}, {total.Elapsed.TotalSeconds:N0} s");
            }
        }

        GC.Collect();
        foreach (var (priority, values) in latencies)
        {
            lock (values)
            {
                if (values.Count > 0)
                    Log($"[D] {priority}: n={values.Count}, mean {values.Average():N0} ms, max {values.Max():N0} ms");
            }
        }

        Log($"[D] failures {failures}; final private {Mb():N0} MB (baseline {baseline:N0}); status {host.Status.State}");
        Assert.Equal(0, failures);
        Assert.True(Mb() < baseline + 600, "private memory grew by more than 600 MB over 100 requests");
    }

    [RealModelFact]
    public async Task Real_E_LoadTimeAndIdleUnload()
    {
        var idle = TimeSpan.FromSeconds(6);
        await using var host = NewHost(idle);
        var before = Mb();

        var sw = Stopwatch.StartNew();
        await host.PreloadAsync();
        Log($"[E] load: {sw.Elapsed.TotalMilliseconds:N0} ms (host measured {host.LastLoadDuration!.Value.TotalMilliseconds:N0}); private {before:N0} -> {Mb():N0} MB");

        var first = await TimedAsync(host, Translate(Sentence));
        Log($"[E] first request after load: first chunk {first.FirstChunkMs:N0} ms, total {first.TotalMs:N0} ms; private {Mb():N0} MB");

        var waited = Stopwatch.StartNew();
        while (host.IsEngineLoaded && waited.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(100);
        GC.Collect();
        Log($"[E] idle unload after {waited.Elapsed.TotalSeconds:N1} s of idle (configured {idle.TotalSeconds:N0} s): loaded={host.IsEngineLoaded}; private {Mb():N0} MB");
        Assert.False(host.IsEngineLoaded);

        var again = await TimedAsync(host, Translate(Sentence));
        Log($"[E] request after unload (reload inside): first chunk {again.FirstChunkMs:N0} ms, total {again.TotalMs:N0} ms; load {host.LastLoadDuration!.Value.TotalMilliseconds:N0} ms; private {Mb():N0} MB");
    }
}
