using System.Text.Json;
using SpeechLib.Diarization;
using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>Runs, layers, people: the speaker data model and its store (design §10.4).</summary>
public class SpeakerDataTests : IDisposable
{
    private readonly SpeakerFixtures _f = new();

    public void Dispose() => _f.Dispose();

    // ── Probabilities ───────────────────────────────────────────────────

    [Fact]
    public void RunProbabilityCodec_ShouldRoundTripAtEightyMilliseconds()
    {
        var run = SpeakerFixtures.Run(10, (0, 4, 0), (4, 10, 1));

        var timeline = RunProbabilityCodec.ToTimeline(run.Probs!);

        Assert.Equal(1000, timeline.FrameCount);
        Assert.True(timeline.Get(100, 0) > 0.99f);
        Assert.True(timeline.Get(100, 1) < 0.01f);
        Assert.True(timeline.Get(700, 1) > 0.99f);
        // 8 bytes per 80 ms: a bit over 360 KB per hour.
        var perHour = RunProbabilityCodec.Pack(RunProbabilityCodec.FromTurns([new RunTurn { Start = 0, End = 3600, Speaker = 0 }], 3600));
        Assert.InRange(Convert.FromBase64String(perHour.Data).Length, 355_000, 365_000);
    }

    // ── Layer ───────────────────────────────────────────────────────────

    [Fact]
    public void SpeakerLayerBuilder_ShouldLabelEveryWordByItsChannel()
    {
        var id = _f.CreateRecording(segments: 4, decoder: true);
        var run = SpeakerFixtures.Run(16, SpeakerFixtures.Alternating(4));

        var built = SpeakerLayerBuilder.Build(run, _f.Store.LoadTranscript(id, "v1"), "v1");

        Assert.Equal(WordTimingKinds.Decoder, built.WordTiming);
        Assert.Equal(4, built.Document.Segments.Count);
        Assert.All(built.Document.Segments, s => Assert.Equal(10, s.Words.Count));
        Assert.Equal(new[] { 0, 1, 0, 1 }, built.Document.Segments.Select(s => s.Speaker));
        Assert.All(built.Document.Segments[1].Words, c => Assert.Equal(1, c));
        Assert.Equal(40, built.KnownWords);
    }

    [Fact]
    public void SpeakerLayerBuilder_ShouldFlagEstimatedTimingsAndSpeakerChanges()
    {
        var id = _f.CreateRecording(segments: 2, decoder: false);
        // Segment 0 (0-3.6 s) is split between two speakers.
        var run = SpeakerFixtures.Run(8, (0, 1.8, 0), (1.8, 3.6, 1), (4, 8, 1));

        var built = SpeakerLayerBuilder.Build(run, _f.Store.LoadTranscript(id, "v1"), "v1");

        Assert.Equal(WordTimingKinds.Estimated, built.WordTiming);
        Assert.True(built.Document.Segments[0].Change);
        Assert.Null(built.Document.Segments[1].Change);
        Assert.Contains(0, built.Document.Segments[0].Words);
        Assert.Contains(1, built.Document.Segments[0].Words);
    }

    [Fact]
    public void SpeakerLayerBuilder_ShouldMarkOverlappedWords()
    {
        var id = _f.CreateRecording(segments: 1);
        var run = SpeakerFixtures.Run(4, (0, 3.6, 0), (0, 3.6, 1));

        var built = SpeakerLayerBuilder.Build(run, _f.Store.LoadTranscript(id, "v1"), "v1");

        var segment = built.Document.Segments[0];
        Assert.NotNull(segment.Overlap);
        Assert.Equal(10, segment.Overlap!.Count);
    }

    // ── Store: runs, layers, people ─────────────────────────────────────

    [Fact]
    public void AddDiarizationRun_ShouldWriteFileAndRegisterPeopleInArrivalOrder()
    {
        var id = _f.CreateRecording();

        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));

        Assert.Equal("r1", run.Id);
        Assert.Equal("diarization/r1.json", run.File);
        Assert.Equal(2, run.Stats!.Speakers);
        var folder = _f.Store.TryGetFolder(id)!;
        Assert.True(File.Exists(Path.Combine(folder, "diarization", "r1.json")));
        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Equal(1, manifest.Schema);
        Assert.Equal(["p1", "p2"], manifest.Speakers!.People.Select(p => p.Id));
        Assert.Equal([1, 2], manifest.Speakers.People.Select(p => p.Number));
        Assert.Equal([0, 1], manifest.Speakers.People.Select(p => p.Color));
        Assert.Equal("p1", manifest.Speakers.Map["r1"]["0"]);
        Assert.Equal("p2", manifest.Speakers.Map["r1"]["1"]);
        Assert.Equal(2, _f.Store.LoadDiarizationRun(id, "r1").Turns.Select(t => t.Speaker).Distinct().Count());
        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SaveSpeakerLayer_ShouldWriteLayerAndMakeItCurrent()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));

        var layer = _f.Label(id, run.Id);

        Assert.Equal("s1", layer.Id);
        Assert.Equal("speakers/v1.r1.json", layer.File);
        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Equal("s1", manifest.CurrentSpeakers["v1"]);
        Assert.Equal("s1", manifest.CurrentSpeakerLayer("v1")!.Id);
        var doc = _f.Store.LoadSpeakerLayer(id, "s1");
        Assert.Equal("v1", doc.Version);
        Assert.Equal("r1", doc.Run);
        Assert.Equal(6, doc.Segments.Count);
    }

    [Fact]
    public void SaveSpeakerLayer_ShouldReplaceTheSameLayerAndKeepOneForANewAssigner()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        _f.Label(id, run.Id);
        _f.Label(id, run.Id);
        Assert.Single(_f.Store.Get(id)!.Manifest.SpeakerLayers);

        var doc = _f.Store.LoadSpeakerLayer(id, "s1");
        _f.Store.SaveSpeakerLayer(id, new SpeakerLayerInfo { Version = "v1", Run = "r1", Assigner = 2 }, doc);

        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Equal(2, manifest.SpeakerLayers.Count);
        Assert.Equal("speakers/v1.r1.a2.json", manifest.SpeakerLayers[1].File);
        Assert.Equal("s2", manifest.CurrentSpeakers["v1"]);
    }

    [Fact]
    public void OneRun_ShouldLabelAnyVersion()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        _f.Label(id, run.Id);
        var edited = _f.Store.SaveEdit(id, "v1", Enumerable.Range(0, 6).Select(i => $"edited text {i} here").ToList());

        var second = _f.Label(id, run.Id, edited.Id);

        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Equal("v2", second.Version);
        Assert.Equal("r1", second.Run);
        Assert.Equal("s1", manifest.CurrentSpeakerLayer("v1")!.Id);
        Assert.Equal("s2", manifest.CurrentSpeakerLayer("v2")!.Id);
    }

    [Fact]
    public void DeleteVersion_ShouldDeleteItsLayersAndAnalysesButKeepTheRun()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        _f.Label(id, run.Id);
        var edited = _f.Store.SaveEdit(id, "v1", Enumerable.Range(0, 6).Select(i => $"edited {i} text").ToList());
        var layer = _f.Label(id, run.Id, edited.Id);
        _f.Store.SaveInsights(id, new AnalysisInfo { SpeakerLayer = layer.Id }, new SpeakerInsightsDocument());
        _f.Store.SetCurrentVersion(id, "v1");

        _f.Store.DeleteVersion(id, "v2");

        var manifest = _f.Store.Get(id)!.Manifest;
        var folder = _f.Store.TryGetFolder(id)!;
        Assert.DoesNotContain(manifest.SpeakerLayers, l => l.Version == "v2");
        Assert.DoesNotContain("v2", manifest.CurrentSpeakers.Keys);
        Assert.Empty(manifest.Analyses);
        Assert.False(File.Exists(Path.Combine(folder, "speakers", "v2.r1.json")));
        Assert.False(File.Exists(Path.Combine(folder, "analysis", "v2.s2.insights.json")));
        Assert.Single(manifest.DiarizationRuns);
        Assert.True(File.Exists(Path.Combine(folder, "diarization", "r1.json")));
    }

    [Fact]
    public void DeleteDiarizationRun_ShouldRefuseTheRunOfACurrentLayerAndDeleteOthers()
    {
        var id = _f.CreateRecording();
        var r1 = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        _f.Label(id, r1.Id);
        var r2 = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        _f.Label(id, r2.Id);

        Assert.Throws<InvalidOperationException>(() => _f.Store.DeleteDiarizationRun(id, r2.Id));
        _f.Store.DeleteDiarizationRun(id, r1.Id);

        var manifest = _f.Store.Get(id)!.Manifest;
        Assert.Equal(["r2"], manifest.DiarizationRuns.Select(r => r.Id));
        Assert.Equal(["s2"], manifest.SpeakerLayers.Select(l => l.Id));
        Assert.False(manifest.Speakers!.Map.ContainsKey("r1"));
        Assert.False(File.Exists(Path.Combine(_f.Store.TryGetFolder(id)!, "diarization", "r1.json")));
    }

    [Fact]
    public void DeleteRecording_ShouldRemoveSpeakerFiles()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        _f.Label(id, run.Id);
        var folder = _f.Store.TryGetFolder(id)!;

        _f.Store.DeleteRecording(id);

        Assert.False(Directory.Exists(folder));
    }

    // ── Names carried over between runs ─────────────────────────────────

    [Fact]
    public void SecondRun_ShouldCarryNamesOverByOverlapEvenWhenChannelsSwap()
    {
        var id = _f.CreateRecording();
        _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        _f.Store.RenamePerson(id, "p1", "Anna");
        _f.Store.RenamePerson(id, "p2", "Mark");

        // The new run numbers the same voices the other way round.
        var swapped = SpeakerFixtures.Alternating(6).Select(t => (t.Start, t.End, 1 - t.Speaker)).ToArray();
        _f.AddRun(id, 24, swapped);

        var directory = _f.Store.Get(id)!.Manifest.Speakers!;
        Assert.Equal(2, directory.People.Count);
        Assert.Equal("p2", directory.Map["r2"]["0"]); // the voice that was channel 1 (Mark) is channel 0 now
        Assert.Equal("p1", directory.Map["r2"]["1"]);
        Assert.Equal(["Anna", "Mark"], directory.People.Select(p => p.Name));
    }

    [Fact]
    public void SecondRun_ShouldCreateANewPersonBelowSixtyPercentOverlap()
    {
        var id = _f.CreateRecording();
        _f.AddRun(id, 24, (0, 12, 0), (12, 24, 1));
        _f.Store.RenamePerson(id, "p1", "Anna");

        // Channel 0 of the new run shares only a third of its speech with the old channel 0.
        _f.AddRun(id, 24, (0, 4, 0), (4, 12, 2), (12, 24, 1));

        var directory = _f.Store.Get(id)!.Manifest.Speakers!;
        Assert.Equal(3, directory.People.Count);
        Assert.Equal("p3", directory.Map["r2"]["0"]);
        Assert.Equal("p1", directory.Map["r2"]["2"]); // 8 of its 8 s were Anna's
        Assert.Equal("Speaker 3", SpeakerDirectory.DisplayName(directory, "p3"));
        Assert.Equal("Anna", SpeakerDirectory.DisplayName(directory, "p1"));
    }

    [Fact]
    public void Hungarian_ShouldPickTheBestPairingNotTheGreedyOne()
    {
        var weights = new double[,] { { 10, 9, 0 }, { 9, 0, 0 }, { 0, 0, 1 } };

        var assignment = HungarianAssignment.Maximize(weights);

        Assert.Equal(new[] { 1, 0, 2 }, assignment);
    }

    // ── Rename, colour, merge, split ────────────────────────────────────

    [Fact]
    public void RenameAndColor_ShouldChangeOnlyTheManifest()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        var layer = _f.Label(id, run.Id);
        var layerFile = Path.Combine(_f.Store.TryGetFolder(id)!, layer.File);
        var before = File.ReadAllBytes(layerFile);

        _f.Store.RenamePerson(id, "p1", "  Anna ");
        _f.Store.SetPersonColor(id, "p2", 11);
        _f.Store.RenamePerson(id, "p2", "  ");

        var people = _f.Store.Get(id)!.Manifest.Speakers!.People;
        Assert.Equal("Anna", people[0].Name);
        Assert.Null(people[1].Name);
        Assert.Equal(3, people[1].Color); // 11 wraps into the 8-colour palette
        Assert.Equal(before, File.ReadAllBytes(layerFile));
        Assert.Throws<KeyNotFoundException>(() => _f.Store.RenamePerson(id, "p9", "x"));
    }

    [Fact]
    public void MergeAndSplit_ShouldRestoreTheChannelMapOfTheRun()
    {
        var id = _f.CreateRecording();
        _f.AddRun(id, 36, (0, 12, 0), (12, 24, 1), (24, 36, 2));
        _f.Store.RenamePerson(id, "p2", "Mark");
        _f.Store.RenamePerson(id, "p3", "Oleg");

        _f.Store.MergePeople(id, "p1", "p2");
        var merged = _f.Store.Get(id)!.Manifest.Speakers!;

        Assert.Equal(["p1", "p3"], merged.People.Select(p => p.Id));
        Assert.Equal("p1", merged.Map["r1"]["1"]);
        Assert.Equal("p3", merged.Map["r1"]["2"]);
        Assert.Equal("Speaker 3", "Speaker " + merged.People[1].Number); // numbering stays stable for p3

        _f.Store.SplitPerson(id, "p1");
        var split = _f.Store.Get(id)!.Manifest.Speakers!;

        Assert.Equal(["p1", "p2", "p3"], split.People.Select(p => p.Id));
        Assert.Equal("Mark", split.People[1].Name);
        Assert.Equal("p2", split.Map["r1"]["1"]);
        Assert.Equal("p1", split.Map["r1"]["0"]);
        Assert.Null(split.People[0].Merged);
    }

    [Fact]
    public void Split_ShouldUndoMergesNewestFirstAndDoNothingWithoutAMerge()
    {
        var id = _f.CreateRecording();
        _f.AddRun(id, 36, (0, 12, 0), (12, 24, 1), (24, 36, 2));
        _f.Store.SplitPerson(id, "p1"); // nothing merged: no-op, no throw

        _f.Store.MergePeople(id, "p1", "p2");
        _f.Store.MergePeople(id, "p1", "p3");
        _f.Store.SplitPerson(id, "p1");

        var directory = _f.Store.Get(id)!.Manifest.Speakers!;
        Assert.Equal(new[] { "p1", "p3" }, directory.People.Select(p => p.Id).Order());
        Assert.Equal("p3", directory.Map["r1"]["2"]);
        Assert.Equal("p1", directory.Map["r1"]["1"]);
    }

    [Fact]
    public void Merge_ShouldMoveOverridesAndFoldPreviouslyMergedPeople()
    {
        var id = _f.CreateRecording();
        _f.AddRun(id, 36, (0, 12, 0), (12, 24, 1), (24, 36, 2));
        _f.Store.AddSpeakerOverride(id, 30, 34, "p3");
        _f.Store.MergePeople(id, "p2", "p3");
        _f.Store.MergePeople(id, "p1", "p2");

        var directory = _f.Store.Get(id)!.Manifest.Speakers!;

        Assert.Equal("p1", directory.Overrides[0].Person);
        Assert.Equal(["p2", "p3"], directory.People[0].Merged!.Select(m => m.Id).Order());
    }

    // ── Compatibility ───────────────────────────────────────────────────

    [Fact]
    public void Manifest_ShouldRoundTripUnknownSpeakerFieldsOfANewerApp()
    {
        var id = _f.CreateRecording();
        var folder = _f.Store.TryGetFolder(id)!;
        var path = Path.Combine(folder, RecordingStore.ManifestFileName);
        var json = JsonNode(File.ReadAllText(path));
        json["speakers"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{ "people": [ { "id": "p1", "name": "Ann", "color": 2, "future": 7 } ], "map": {}, "overrides": [], "suggested": [], "extra": { "a": 1 } }""");
        json["diarizationRuns"] = System.Text.Json.Nodes.JsonNode.Parse(
            """[ { "id": "r1", "file": "diarization/r1.json", "source": "live", "newField": true, "stats": { "speakers": 2, "future": 1 } } ]""");
        json["somethingNew"] = 5;
        File.WriteAllText(path, json.ToJsonString());

        _f.Store.Rename(id, "Weekly"); // load-modify-write by this version
        var written = File.ReadAllText(path);

        Assert.Contains("\"future\": 7", written);
        Assert.Contains("\"extra\"", written);
        Assert.Contains("\"newField\": true", written);
        Assert.Contains("\"somethingNew\": 5", written);
        Assert.Equal(1, _f.Store.Get(id)!.Manifest.Schema);
    }

    [Fact]
    public void OldManifestWithoutSpeakers_ShouldLoadWithEmptySpeakerData()
    {
        var id = _f.CreateRecording();

        var manifest = _f.Store.Get(id)!.Manifest;

        Assert.Empty(manifest.DiarizationRuns);
        Assert.Empty(manifest.SpeakerLayers);
        Assert.Empty(manifest.CurrentSpeakers);
        Assert.Null(manifest.Speakers);
        Assert.Null(manifest.CurrentSpeakerLayer("v1"));
    }

    [Fact]
    public void LayerAndRunFiles_ShouldKeepUnknownFields()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 24, SpeakerFixtures.Alternating(6));
        var layer = _f.Label(id, run.Id);
        var layerPath = Path.Combine(_f.Store.TryGetFolder(id)!, layer.File);
        var text = File.ReadAllText(layerPath).Replace("\"schema\": 1", "\"schema\": 1, \"later\": \"x\"");
        File.WriteAllText(layerPath, text);

        var doc = _f.Store.LoadSpeakerLayer(id, layer.Id);
        _f.Store.SaveSpeakerLayer(id, layer, doc);

        Assert.Contains("\"later\": \"x\"", File.ReadAllText(layerPath));
    }

    [Fact]
    public void EditedVersion_ShouldNotInheritTheChainOrigin()
    {
        var id = _f.CreateRecording();
        var spec = HistoryFixtures.LiveSpec();
        spec.Kind = VersionKinds.Rerecognized;
        spec.Origin = new VersionOrigin { ChainId = "ch1", Trigger = "auto", Steps = ["rerecognize", "diarize"] };
        var version = _f.Store.AddVersion(id, spec, _f.Store.LoadTranscript(id, "v1"));

        var edited = _f.Store.SaveEdit(id, version.Id, Enumerable.Range(0, 6).Select(i => $"text {i}").ToList());

        Assert.Equal("ch1", _f.Store.Get(id)!.Manifest.FindVersion(version.Id)!.Origin!.ChainId);
        Assert.Null(_f.Store.Get(id)!.Manifest.FindVersion(edited.Id)!.Origin);
    }

    // ── Views, statistics, overrides ────────────────────────────────────

    [Fact]
    public void Stats_ShouldCountTalkTimePercentAndTurnsPerPerson()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 40, (0, 10, 0), (10, 14, 1), (14, 20, 0), (30, 40, 0));
        var manifest = _f.Store.Get(id)!.Manifest;

        var stats = SpeakerViews.Stats(manifest, run.Id, _f.Store.LoadDiarizationRun(id, run.Id).Turns);

        Assert.Equal("p1", stats[0].PersonId);
        Assert.Equal(26, stats[0].TalkSeconds);
        Assert.Equal(86.7, stats[0].Percent);
        // 0-10 and 14-20 are 4 s apart (> 1 s): three turns, 20-30 silence.
        Assert.Equal(3, stats[0].Turns);
        Assert.Equal(4, stats[1].TalkSeconds);
        Assert.Equal(1, stats[1].Turns);
    }

    [Fact]
    public void Stats_ShouldCountMergedChannelsOfOnePersonOnce()
    {
        var id = _f.CreateRecording();
        var run = _f.AddRun(id, 20, (0, 10, 0), (5, 15, 1));
        _f.Store.MergePeople(id, "p1", "p2");
        var manifest = _f.Store.Get(id)!.Manifest;

        var stats = SpeakerViews.Stats(manifest, run.Id, _f.Store.LoadDiarizationRun(id, run.Id).Turns);

        var only = Assert.Single(stats);
        Assert.Equal(15, only.TalkSeconds);
        Assert.Equal(100, only.Percent);
        Assert.Equal(1, only.Turns);
    }

    [Fact]
    public void Build_ShouldMapChannelsToPeopleApplyOverridesAndFlagChange()
    {
        var id = _f.CreateRecording(segments: 3);
        var run = _f.AddRun(id, 12, (0, 1.8, 0), (1.8, 4, 1), (4, 8, 1), (8, 12, 0));
        var layer = _f.Label(id, run.Id);
        _f.Store.RenamePerson(id, "p2", "Mark");
        _f.Store.AddSpeakerOverride(id, 8, 12, "p2");
        var manifest = _f.Store.Get(id)!.Manifest;

        var view = SpeakerViews.Build(manifest, layer, _f.Store.LoadSpeakerLayer(id, layer.Id), _f.Store.LoadDiarizationRun(id, run.Id));

        Assert.Equal(layer.Id, view.LayerId);
        Assert.Equal("r1", view.RunId);
        Assert.Equal("v1", view.VersionId);
        Assert.Equal("Nemotron 3 Diarization INT8", view.ModelTitle);
        Assert.Equal("offline", view.Preset);
        Assert.Equal(["Speaker 1", "Mark"], view.People.Select(p => p.Name));
        Assert.True(view.Segments[0].HasChange);
        Assert.Equal("p2", view.Segments[1].PersonId);
        Assert.Equal("p2", view.Segments[2].PersonId);       // the override moved the last segment to Mark
        Assert.All(view.Segments[2].WordPersonIds, p => Assert.Equal("p2", p));
        Assert.Equal(10, view.Segments[1].WordPersonIds.Count);
        Assert.Equal(2, view.Stats.Count);
    }

    [Fact]
    public void ChannelStats_ShouldCountWordsAndTalkPerChannel()
    {
        var id = _f.CreateRecording(segments: 4);
        var run = _f.AddRun(id, 16, SpeakerFixtures.Alternating(4));
        var layer = _f.Label(id, run.Id);

        var stats = SpeakerViews.ChannelStats(_f.Store.LoadDiarizationRun(id, run.Id), _f.Store.LoadSpeakerLayer(id, layer.Id));

        Assert.Equal(20, stats["0"].Words);
        Assert.Equal(20, stats["1"].Words);
        Assert.Equal(8, stats["0"].TalkSec);
        Assert.Equal(2, stats["1"].Turns);
    }

    private static System.Text.Json.Nodes.JsonObject JsonNode(string text) =>
        System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
}
