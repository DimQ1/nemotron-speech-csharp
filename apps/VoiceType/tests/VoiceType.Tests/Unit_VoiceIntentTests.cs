using VoiceType.Models;
using VoiceType.Services;
using VoiceType.Services.Intents;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// Unit tests for the Voice Intent Mode: L0 heuristic scorer, hysteresis,
/// router, window-target history, settings serialization, and voice selection
/// in the clarify dialog. No model, no Win32, no network.
/// </summary>
public sealed class Unit_VoiceIntentTests
{
    private static VoiceIntentSettings Settings(bool enabled = true) => new()
    {
        Enabled = enabled,
        ConfidenceThreshold = 0.85f,
        HysteresisM = 2,
        HysteresisN = 3,
    };

    // ── HeuristicScorer ─────────────────────────────────────────

    [Theory]
    [InlineData("начни печатать", VoiceIntentIds.StartTyping)]
    [InlineData("пожалуйста начать печатать текст сейчас", VoiceIntentIds.StartTyping)]
    [InlineData("start typing please", VoiceIntentIds.StartTyping)]
    [InlineData("begin typing", VoiceIntentIds.StartTyping)]
    public void Heuristic_StartTyping_RuEn(string text, string expected)
    {
        var scores = HeuristicScorer.Score(text, VoiceIntentTaxonomy.Default);
        Assert.Equal(expected, scores.TopIntent);
        Assert.True(scores.TopScore >= 0.85f);
    }

    [Theory]
    [InlineData("стоп печать", VoiceIntentIds.StopTyping)]
    [InlineData("прекрати печатать", VoiceIntentIds.StopTyping)]
    [InlineData("stop typing", VoiceIntentIds.StopTyping)]
    [InlineData("stop dictation now", VoiceIntentIds.StopTyping)]
    public void Heuristic_StopTyping_RuEn(string text, string expected)
    {
        var scores = HeuristicScorer.Score(text, VoiceIntentTaxonomy.Default);
        Assert.Equal(expected, scores.TopIntent);
    }

    [Theory]
    [InlineData("набери в блокнот", VoiceIntentIds.SwitchTarget)]
    [InlineData("type this in vs code", VoiceIntentIds.SwitchTarget)]
    [InlineData("напечатай в приложении код", VoiceIntentIds.SwitchTarget)]
    public void Heuristic_SwitchTarget_RuEn(string text, string expected)
    {
        var scores = HeuristicScorer.Score(text, VoiceIntentTaxonomy.Default);
        Assert.Equal(expected, scores.TopIntent);
    }

    [Theory]
    [InlineData("отмена", VoiceIntentIds.Cancel)]
    [InlineData("cancel", VoiceIntentIds.Cancel)]
    [InlineData("never mind", VoiceIntentIds.Cancel)]
    [InlineData("забудь", VoiceIntentIds.Cancel)]
    public void Heuristic_Cancel_RuEn(string text, string expected)
    {
        var scores = HeuristicScorer.Score(text, VoiceIntentTaxonomy.Default);
        Assert.Equal(expected, scores.TopIntent);
    }

    [Theory]
    [InlineData("привет как дела")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    [InlineData("сегодня хорошая погода для прогулки")]
    [InlineData("hello world this is a dictation test")]
    public void Heuristic_PlainText_IsDictation(string text)
    {
        var scores = HeuristicScorer.Score(text, VoiceIntentTaxonomy.Default);
        Assert.Equal(VoiceIntentIds.Dictation, scores.TopIntent);
    }

    // ── HysteresisFilter ────────────────────────────────────────

    [Fact]
    public void Hysteresis_EntersAfterMofN()
    {
        var filter = new HysteresisFilter(m: 2, n: 3, threshold: 0.85f);

        Assert.False(filter.Update(0.9f)); // 1 of 1
        Assert.True(filter.Update(0.9f));  // 2 of 2 → active
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void Hysteresis_SingleSpike_DoesNotActivate()
    {
        var filter = new HysteresisFilter(m: 2, n: 3, threshold: 0.85f);

        filter.Update(0.95f); // spike
        Assert.False(filter.Update(0.1f)); // back below
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void Hysteresis_Exits_WhenFullWindowFallsBelow()
    {
        var filter = new HysteresisFilter(m: 2, n: 3, threshold: 0.85f);

        filter.Update(0.9f);
        filter.Update(0.9f);
        Assert.True(filter.IsActive);

        filter.Update(0.1f);
        filter.Update(0.1f);
        Assert.False(filter.Update(0.1f)); // window full of misses → exit
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void Hysteresis_Reset_ClearsState()
    {
        var filter = new HysteresisFilter(m: 1, n: 1, threshold: 0.5f);
        filter.Update(0.9f);
        Assert.True(filter.IsActive);
        filter.Reset();
        Assert.False(filter.IsActive);
    }

    // ── VoiceIntentRouter (L0-only, no model) ───────────────────

    [Fact]
    public async Task Router_StartTyping_IsCommand()
    {
        using var router = new VoiceIntentRouter(Settings(), l1: null);
        var result = await router.RouteAsync("начни печатать");

        Assert.True(result.IsCommand);
        Assert.Equal(VoiceIntentIds.StartTyping, result.Intent);
    }

    [Fact]
    public async Task Router_StopTyping_IsCommand()
    {
        using var router = new VoiceIntentRouter(Settings(), l1: null);
        var result = await router.RouteAsync("stop typing");

        Assert.True(result.IsCommand);
        Assert.Equal(VoiceIntentIds.StopTyping, result.Intent);
    }

    [Fact]
    public async Task Router_SwitchTarget_IsCommand()
    {
        using var router = new VoiceIntentRouter(Settings(), l1: null);
        var result = await router.RouteAsync("набери в блокнот");

        Assert.True(result.IsCommand);
        Assert.Equal(VoiceIntentIds.SwitchTarget, result.Intent);
    }

    [Fact]
    public async Task Router_PlainDictation_IsNotCommand()
    {
        using var router = new VoiceIntentRouter(Settings(), l1: null);
        var result = await router.RouteAsync("привет это обычный текст для диктовки");

        Assert.False(result.IsCommand);
        Assert.Equal(VoiceIntentIds.Dictation, result.Intent);
    }

    [Fact]
    public async Task Router_EmptyText_IsDictation()
    {
        using var router = new VoiceIntentRouter(Settings(), l1: null);
        var result = await router.RouteAsync("   ");

        Assert.False(result.IsCommand);
        Assert.Equal(VoiceIntentIds.Dictation, result.Intent);
    }

    [Fact]
    public async Task Router_L0Trigger_BypassesHysteresis()
    {
        // A confident L0 lexical trigger must fire on a single utterance —
        // commands are discrete, not a continuous state needing M-of-N.
        using var router = new VoiceIntentRouter(Settings(), l1: null);
        var result = await router.RouteAsync("стоп печать");

        Assert.True(result.IsCommand);
        Assert.Equal(VoiceIntentIds.StopTyping, result.Intent);
    }

    // ── Taxonomy ────────────────────────────────────────────────

    [Fact]
    public void Taxonomy_Default_HasAllCommandIntents()
    {
        var taxonomy = VoiceIntentTaxonomy.Default;
        Assert.NotNull(taxonomy.Find(VoiceIntentIds.StartTyping));
        Assert.NotNull(taxonomy.Find(VoiceIntentIds.StopTyping));
        Assert.NotNull(taxonomy.Find(VoiceIntentIds.SwitchTarget));
        Assert.NotNull(taxonomy.Find(VoiceIntentIds.Cancel));
    }

    [Fact]
    public void Taxonomy_Load_MissingFile_FallsBackToDefault()
    {
        var taxonomy = VoiceIntentTaxonomy.Load(@"Z:\nonexistent\intents.json");
        Assert.NotNull(taxonomy.Find(VoiceIntentIds.StartTyping));
    }

    [Fact]
    public void Taxonomy_Load_CustomFile_OverridesDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"intents-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                [ { "id": "start_typing", "exemplars": ["go"], "lexicalTriggers": ["go go go"] } ]
                """);
            var taxonomy = VoiceIntentTaxonomy.Load(path);
            var start = taxonomy.Find(VoiceIntentIds.StartTyping);
            Assert.NotNull(start);
            Assert.Contains("go", start.Exemplars);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── WindowTargetHistory ─────────────────────────────────────

    private static WindowTargetInfo Info(string proc, string title, string? autoId = null, string? name = null)
        => new(nint.Zero, proc, title, autoId, "Edit", name);

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"targets-{Guid.NewGuid():N}.json");

    [Fact]
    public void History_Record_AddsTarget()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 10);
            history.Record(Info("notepad", "Untitled - Notepad", "15", "Text Editor"));

            Assert.Single(history.Targets);
            Assert.Equal("notepad", history.Targets[0].ProcessName);
            Assert.Equal(1, history.Targets[0].UseCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_Record_SameTarget_IncrementsUseCount()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 10);
            history.Record(Info("notepad", "Untitled", "15", "Editor"));
            history.Record(Info("notepad", "Untitled", "15", "Editor"));

            Assert.Single(history.Targets);
            Assert.Equal(2, history.Targets[0].UseCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_Record_DifferentFields_KeptSeparately()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 10);
            history.Record(Info("harness", "Test", "Field1", "Поле 1"));
            history.Record(Info("harness", "Test", "Field2", "Поле 2"));

            Assert.Equal(2, history.Targets.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_LruTrims_ToPerProcessCap()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 3);
            for (int i = 0; i < 5; i++)
                history.Record(Info("app", "T", $"id{i}", $"field{i}"));

            var forApp = history.Targets.Where(t => t.ProcessName == "app").ToList();
            Assert.Equal(3, forApp.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_FindCandidates_ByProcessName()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 10);
            history.Record(Info("notepad", "Untitled", "15", "Editor"));
            history.Record(Info("code", "file.cs - VS Code", "editor", "Editor"));

            var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var found = history.FindCandidates("notepad", aliases);

            Assert.Single(found);
            Assert.Equal("notepad", found[0].ProcessName);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_FindCandidates_ByAlias()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 10);
            history.Record(Info("code", "file.cs - VS Code", "editor", "Editor"));

            var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["code"] = new[] { "vs code", "visual studio code" },
            };
            var found = history.FindCandidates("visual studio code", aliases);

            Assert.Single(found);
            Assert.Equal("code", found[0].ProcessName);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_FindCandidates_UnknownApp_ReturnsEmpty()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 10);
            history.Record(Info("notepad", "Untitled", "15", "Editor"));

            var found = history.FindCandidates("some random app xyz", new Dictionary<string, string[]>());
            Assert.Empty(found);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_Persistence_RoundTrips()
    {
        var path = TempFile();
        try
        {
            var first = new WindowTargetHistory(path, perProcessCap: 10);
            first.Record(Info("notepad", "Untitled", "15", "Editor"));

            var second = new WindowTargetHistory(path, perProcessCap: 10);
            Assert.Single(second.Targets);
            Assert.Equal("notepad", second.Targets[0].ProcessName);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void History_Clear_RemovesAll()
    {
        var path = TempFile();
        try
        {
            var history = new WindowTargetHistory(path, perProcessCap: 10);
            history.Record(Info("notepad", "Untitled", "15", "Editor"));
            history.Clear();

            Assert.Empty(history.Targets);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── VoiceIntentSettings serialization ───────────────────────

    [Fact]
    public void Settings_Defaults_AreDisabled()
    {
        var settings = new VoiceIntentSettings();
        Assert.False(settings.Enabled);
        Assert.Equal("", settings.IntentModelPath);
        Assert.Equal(0.85f, settings.ConfidenceThreshold);
        Assert.True(settings.RememberTargets);
    }

    [Fact]
    public void AppSettings_VoiceIntents_RoundTrips_Json()
    {
        var settings = new AppSettings
        {
            VoiceIntents = new VoiceIntentSettings
            {
                Enabled = true,
                ConfidenceThreshold = 0.9f,
                HysteresisM = 3,
                HysteresisN = 5,
                RememberTargets = false,
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.True(loaded.VoiceIntents.Enabled);
        Assert.Equal(0.9f, loaded.VoiceIntents.ConfidenceThreshold);
        Assert.Equal(3, loaded.VoiceIntents.HysteresisM);
        Assert.False(loaded.VoiceIntents.RememberTargets);
    }

    [Fact]
    public void AppSettings_WithoutVoiceIntents_Deserializes_WithDefaults()
    {
        // Simulates an old settings.json that predates the VoiceIntents section.
        var json = """{ "SelectedModel": "x", "Language": "auto" }""";
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded.VoiceIntents);
        Assert.False(loaded.VoiceIntents.Enabled);
    }

    [Fact]
    public void AppSettings_DefaultAliases_ContainCommonApps()
    {
        var settings = new VoiceIntentSettings();
        Assert.True(settings.AppAliases.ContainsKey("code"));
        Assert.True(settings.AppAliases.ContainsKey("notepad"));
    }

    // ── ClarifyTargetViewModel voice selection ──────────────────

    private static WindowTarget T(string proc, string field) =>
        new(proc, "Title", field, "Edit", field, DateTimeOffset.Now, 1);

    [Theory]
    [InlineData("1", 0)]
    [InlineData("first", 0)]
    [InlineData("первое", 0)]
    [InlineData("one", 0)]
    public void Clarify_VoiceSelects_First(string text, int expectedIndex)
    {
        var vm = new VoiceType.ViewModels.ClarifyTargetViewModel("app",
            new[] { T("app", "A"), T("app", "B") });

        Assert.True(vm.TrySelectByVoice(text));
        Assert.Equal(expectedIndex, vm.SelectedIndex);
    }

    [Theory]
    [InlineData("2", 1)]
    [InlineData("second", 1)]
    [InlineData("второе", 1)]
    [InlineData("поле 2", 1)]
    public void Clarify_VoiceSelects_Second(string text, int expectedIndex)
    {
        var vm = new VoiceType.ViewModels.ClarifyTargetViewModel("app",
            new[] { T("app", "A"), T("app", "B") });

        Assert.True(vm.TrySelectByVoice(text));
        Assert.Equal(expectedIndex, vm.SelectedIndex);
    }

    [Fact]
    public void Clarify_VoiceSelect_OutOfRange_ReturnsFalse()
    {
        var vm = new VoiceType.ViewModels.ClarifyTargetViewModel("app",
            new[] { T("app", "A") });

        Assert.False(vm.TrySelectByVoice("5"));
        Assert.False(vm.TrySelectByVoice("третье"));
    }

    [Fact]
    public void Clarify_VoiceSelect_NonNumber_ReturnsFalse()
    {
        var vm = new VoiceType.ViewModels.ClarifyTargetViewModel("app",
            new[] { T("app", "A"), T("app", "B") });

        Assert.False(vm.TrySelectByVoice("some unrelated dictation text"));
    }

    // ── WindowTarget.DisplayLabel ───────────────────────────────

    [Fact]
    public void DisplayLabel_PrefersElementName()
    {
        var t = new WindowTarget("notepad", "Untitled", "15", "Edit", "Text Editor", DateTimeOffset.Now, 1);
        Assert.Contains("Text Editor", t.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_FallsBackToTitle_WhenNoField()
    {
        var t = new WindowTarget("notepad", "Untitled - Notepad", null, null, null, DateTimeOffset.Now, 1);
        Assert.Contains("Untitled - Notepad", t.DisplayLabel);
    }
}
