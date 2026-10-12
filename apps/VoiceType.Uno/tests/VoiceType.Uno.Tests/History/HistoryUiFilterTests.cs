using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryUiFilterTests
{
    private static readonly DateTimeOffset T0 = HistoryFixtures.T0;

    private static RecordingManifest Manifest(string id, int minutesAgo, double duration, string language, string folder, bool inject = false) => new()
    {
        Id = id,
        StartedAt = T0.AddMinutes(-minutesAgo),
        Mode = new RecordingMode { InjectText = inject },
        Audio = new AudioInfo { File = "audio.ogg", DurationSec = duration },
        CurrentVersion = "v1",
        Versions =
        [
            new TranscriptVersion
            {
                Id = "v1",
                Model = new ModelInfo { Folder = folder, Title = folder.StartsWith("nemo") ? "Nemotron ASR" : "Parakeet TDT", Variant = "INT4" },
                Language = new LanguageInfo { Requested = "auto", Detected = language },
                Excerpt = "budget meeting " + (id == "b" ? "zebra" : "lion"),
            },
        ],
    };

    private static (List<HistorySearchDocument> Docs, Dictionary<string, RecordingManifest> Map) Data()
    {
        var manifests = new[]
        {
            Manifest("a", 10, 60, "en", "parakeet-int4"),
            Manifest("b", 20, 600, "ru", "nemotron-int4", inject: true),
            Manifest("c", 30, 120, "en", "nemotron-int4"),
        };
        return (manifests.Select(m => HistorySearch.CreateDocument(m, null)).ToList(), manifests.ToDictionary(m => m.Id));
    }

    [Fact]
    public void Default_ShouldNotBeActive()
    {
        Assert.False(HistoryUiFilterOptions.Default.IsActive);
        Assert.Empty(HistoryUiFilterOptions.Default.Chips());
    }

    [Fact]
    public void Chips_ShouldDescribeEveryActiveFilterAndBeRemovable()
    {
        var options = new HistoryUiFilterOptions
        {
            WithTranslation = true, InjectOnly = true, Language = "en", Model = "parakeet-int4", ModelLabel = "Parakeet INT4",
            Sort = HistoryUiSort.LongestFirst,
        };

        Assert.Equal(["With translation", "Inject dictations", "Language: EN", "Model: Parakeet INT4", "Longest first"],
            options.Chips().Select(c => c.Label));

        var reduced = options.Without(HistoryUiFilterOptions.LanguageKey).Without(HistoryUiFilterOptions.SortKey);
        Assert.Null(reduced.Language);
        Assert.Equal(HistoryUiSort.NewestFirst, reduced.Sort);
        Assert.Equal(3, reduced.Chips().Count);
    }

    [Fact]
    public void Run_ShouldApplyLanguageModelAndInject()
    {
        var (docs, map) = Data();

        Assert.Equal(["a", "c"], HistoryUiQuery.Run(docs, map, "", new HistoryUiFilterOptions { Language = "EN" }).Select(h => h.Document.RecordingId));
        Assert.Equal(["b", "c"], HistoryUiQuery.Run(docs, map, null, new HistoryUiFilterOptions { Model = "nemotron-int4" }).Select(h => h.Document.RecordingId));
        Assert.Equal(["b"], HistoryUiQuery.Run(docs, map, null, new HistoryUiFilterOptions { InjectOnly = true }).Select(h => h.Document.RecordingId));
    }

    [Fact]
    public void Run_ShouldSortAsRequested()
    {
        var (docs, map) = Data();

        Assert.Equal(["a", "b", "c"], HistoryUiQuery.Run(docs, map, null, HistoryUiFilterOptions.Default).Select(h => h.Document.RecordingId));
        Assert.Equal(["c", "b", "a"], HistoryUiQuery.Run(docs, map, null, new HistoryUiFilterOptions { Sort = HistoryUiSort.OldestFirst }).Select(h => h.Document.RecordingId));
        Assert.Equal(["b", "c", "a"], HistoryUiQuery.Run(docs, map, null, new HistoryUiFilterOptions { Sort = HistoryUiSort.LongestFirst }).Select(h => h.Document.RecordingId));
    }

    [Fact]
    public void Run_ShouldSearchWithTheQuery()
    {
        var (docs, map) = Data();
        Assert.Equal(["b"], HistoryUiQuery.Run(docs, map, "meeting zebra", HistoryUiFilterOptions.Default).Select(h => h.Document.RecordingId));
    }

    [Fact]
    public void Choices_ShouldListLanguagesAndModelsPresent()
    {
        var (docs, map) = Data();

        Assert.Equal(["Any language", "EN", "RU"], HistoryUiQuery.LanguageChoices(docs).Select(c => c.Label));
        var models = HistoryUiQuery.ModelChoices(map.Values);
        Assert.Equal(["Any model", "Nemotron INT4", "Parakeet INT4"], models.Select(c => c.Label));
        Assert.Equal("nemotron-int4", models[1].Value);
    }
}
