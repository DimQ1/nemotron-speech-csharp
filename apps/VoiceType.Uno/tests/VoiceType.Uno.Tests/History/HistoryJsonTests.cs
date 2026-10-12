using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryJsonTests
{
    private static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Serialize(value, info);

    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(json, info)!;

    [Fact]
    public void RecordingManifest_ShouldRoundTripEveryField()
    {
        var manifest = new RecordingManifest
        {
            Id = "7f3a9c",
            Title = "Weekly sync",
            AutoTitle = "Team meeting",
            StartedAt = HistoryFixtures.T0,
            EndedAt = HistoryFixtures.T0.AddMinutes(13),
            State = RecordingStates.Complete,
            Mode = new RecordingMode { InjectText = true, Translation = true },
            Audio = new AudioInfo
            {
                File = "audio.ogg", Codec = "opus", SampleRate = 16000, Channels = 1, Bitrate = 24000,
                DurationSec = 754.2, Bytes = 2_270_000, Source = "Mic",
                Gaps = [new AudioGap { AtSec = 312.4, WallSec = 41, Reason = "muted" }],
                RemovedAt = HistoryFixtures.T0.AddDays(30),
            },
            CurrentVersion = "v2",
            Versions =
            [
                HistoryFixtures.LiveSpec(),
                new TranscriptVersion
                {
                    Id = "v2", Kind = VersionKinds.Edited, File = "transcripts/v2.json", Parent = "v1",
                    Stats = new VersionStats { Words = 10, Segments = 2, Rtf = 0.19, ElapsedSec = 141 },
                    Excerpt = "Okay, let's start.",
                },
            ],
            Translations =
            [
                new TranslationInfo
                {
                    Id = "t1", File = "translations/t1.ru.v2.json", SourceVersion = "v2", TargetLanguage = "ru",
                    Engine = new TranslationEngineInfo { Kind = "native", Model = "gemma", Backend = "cpu", Greedy = true },
                    SystemPromptHash = "sha256:abc", CreatedAt = HistoryFixtures.T0, Status = TranslationStatuses.Partial,
                },
            ],
        };
        manifest.Versions[0].Id = "v1";
        manifest.Versions[0].File = "transcripts/v1.json";

        var json = Serialize(manifest, HistoryJsonContext.Default.RecordingManifest);
        var back = Deserialize(json, HistoryJsonContext.Default.RecordingManifest);

        Assert.Equal(json, Serialize(back, HistoryJsonContext.Default.RecordingManifest));
        Assert.Equal("Weekly sync", back.EffectiveTitle);
        Assert.Equal("v2", back.Current!.Id);
        Assert.True(back.Mode.InjectText);
        Assert.Equal(41, back.Audio!.Gaps[0].WallSec);
        Assert.False(back.HasAudio);
        Assert.Equal("1.1", JsonValues.ToDisplay(back.Versions[0].Settings!["repetitionPenalty"]));
        Assert.Contains("\"currentVersion\": \"v2\"", json);
        Assert.Contains("\"schema\": 1", json);
    }

    [Fact]
    public void RecordingManifest_ShouldKeepUnknownFieldsFromNewerApp()
    {
        const string json = """
            {
              "schema": 2,
              "id": "abc123",
              "startedAt": "2026-10-12T14:02:11+03:00",
              "futureTopLevel": { "x": 1 },
              "mode": { "injectText": false, "futureMode": true },
              "audio": { "file": "audio.ogg", "futureAudio": [1, 2] },
              "currentVersion": "v1",
              "versions": [ { "id": "v1", "kind": "aligned", "file": "transcripts/v1.json", "futureVersion": "y" } ],
              // comments and trailing commas are tolerated
              "translations": [],
            }
            """;

        var manifest = Deserialize(json, HistoryJsonContext.Default.RecordingManifest);
        var written = JsonNode.Parse(Serialize(manifest, HistoryJsonContext.Default.RecordingManifest))!;

        Assert.Equal(2, manifest.Schema);
        Assert.Equal("aligned", manifest.Versions[0].Kind);
        Assert.Equal(1, written["futureTopLevel"]!["x"]!.GetValue<int>());
        Assert.True(written["mode"]!["futureMode"]!.GetValue<bool>());
        Assert.Equal(2, written["audio"]!["futureAudio"]!.AsArray().Count);
        Assert.Equal("y", written["versions"]![0]!["futureVersion"]!.GetValue<string>());
    }

    [Fact]
    public void TranscriptDocument_ShouldUseCompactWordNamesAndRoundTrip()
    {
        var doc = HistoryFixtures.Transcript((0, 2, "Okay, let's start."));
        doc.Version = "v1";
        doc.Segments[0].WordTiming = WordTimingKinds.Decoder;

        var json = Serialize(doc, HistoryJsonContext.Default.TranscriptDocument);
        var back = Deserialize(json, HistoryJsonContext.Default.TranscriptDocument);

        Assert.Contains("\"i\": 0", json);
        Assert.Contains("\"w\": \"Okay,\"", json);
        Assert.Contains("\"s\": 0", json);
        Assert.Equal(json, Serialize(back, HistoryJsonContext.Default.TranscriptDocument));
        Assert.Equal(3, back.Segments[0].Words.Count);
        Assert.Equal(WordTimingKinds.Decoder, back.Segments[0].WordTiming);
    }

    [Fact]
    public void TranscriptDocument_ShouldKeepUnknownWordFields()
    {
        const string json = """{"version":"v1","segments":[{"i":0,"text":"hi","words":[{"w":"hi","s":0,"e":1,"conf":0.9}],"speaker":"A"}]}""";

        var doc = Deserialize(json, HistoryJsonContext.Default.TranscriptDocument);
        var written = JsonNode.Parse(Serialize(doc, HistoryJsonContext.Default.TranscriptDocument))!;

        Assert.Equal(0.9, written["segments"]![0]!["words"]![0]!["conf"]!.GetValue<double>());
        Assert.Equal("A", written["segments"]![0]!["speaker"]!.GetValue<string>());
    }

    [Fact]
    public void TranslationDocument_ShouldRoundTripAndKeepUnknownFields()
    {
        const string json = """{"sourceVersion":"v2","targetLanguage":"ru","segments":[{"i":3,"text":"Привет","alt":"x"}],"sentencesDone":1,"sentencesTotal":4,"extra":true}""";

        var doc = Deserialize(json, HistoryJsonContext.Default.TranslationDocument);
        var written = JsonNode.Parse(Serialize(doc, HistoryJsonContext.Default.TranslationDocument))!;

        Assert.Equal("Привет", doc.TextFor(3));
        Assert.Null(doc.TextFor(0));
        Assert.Equal(4, doc.SentencesTotal);
        Assert.True(written["extra"]!.GetValue<bool>());
        Assert.Equal("x", written["segments"]![0]!["alt"]!.GetValue<string>());
    }

    [Fact]
    public void ChatRecord_ShouldRoundTripAgentSessionVerbatim()
    {
        using var session = JsonDocument.Parse("""{"messages":[{"role":"user","contents":[{"$type":"text","text":"hi"}]}],"state":{"n":3}}""");
        var chat = new ChatRecord
        {
            Id = "c1", Title = "About the sync", CreatedAt = HistoryFixtures.T0, UpdatedAt = HistoryFixtures.T0,
            Sources = [new ChatSource("7f3a9c", "v2", HistoryFixtures.T0), new ChatSource("0a0b0c", "v1", HistoryFixtures.T0)],
            Messages =
            [
                new ChatMessage
                {
                    Id = "m1", Role = ChatRoles.Assistant, Text = "Monday at ten.", CreatedAt = HistoryFixtures.T0,
                    Citations = [new ChatCitation { RecordingId = "7f3a9c", VersionId = "v2", StartSec = 200, EndSec = 205, Quote = "ten" }],
                },
            ],
            AgentSession = session.RootElement.Clone(),
        };

        var json = Serialize(chat, HistoryJsonContext.Default.ChatRecord);
        var back = Deserialize(json, HistoryJsonContext.Default.ChatRecord);

        Assert.Equal(json, Serialize(back, HistoryJsonContext.Default.ChatRecord));
        Assert.Equal(3, back.AgentSession!.Value.GetProperty("state").GetProperty("n").GetInt32());
        Assert.Equal(2, back.Sources.Count);
        Assert.True(back.Includes("0a0b0c"));
        Assert.Equal(200, back.Messages[0].Citations[0].StartSec);
    }

    [Fact]
    public void ChatRecord_ShouldLoadWithoutAgentSession()
    {
        var chat = Deserialize("""{"id":"c1","title":"t","agentSession":null,"futureField":1}""", HistoryJsonContext.Default.ChatRecord);

        Assert.Null(chat.AgentSession);
        Assert.NotNull(chat.ExtensionData);
    }

    [Fact]
    public void JobQueueDocument_ShouldRoundTripWithCamelCaseEnums()
    {
        var doc = new JobQueueDocument
        {
            Jobs =
            [
                new HistoryJob
                {
                    Id = "j1", Kind = HistoryJobKind.AutoTitle, BatchId = "b1", RecordingId = "7f3a9c",
                    Parameters = new JobParameters
                    {
                        ModelFolder = "parakeet", CatalogRepoId = "repo/x", Language = "ru", TargetLanguage = "en",
                        Settings = JsonValues.Settings(("useVad", false)), SourceVersion = "v1",
                    },
                    Priority = JobPriorities.Low,
                    Condition = JobCondition.Window(new TimeOnly(22, 0), new TimeOnly(6, 0)),
                    State = JobState.Paused, Progress = 0.42, Attempts = 2, Error = "x", ResultId = "v3",
                    CreatedAt = HistoryFixtures.T0, UpdatedAt = HistoryFixtures.T0,
                },
            ],
        };

        var json = Serialize(doc, HistoryJsonContext.Default.JobQueueDocument);
        var back = Deserialize(json, HistoryJsonContext.Default.JobQueueDocument);

        Assert.Contains("\"kind\": \"autoTitle\"", json);
        Assert.Contains("\"state\": \"paused\"", json);
        Assert.Contains("\"kind\": \"atTime\"", json);
        Assert.Equal(json, Serialize(back, HistoryJsonContext.Default.JobQueueDocument));
        Assert.Equal(new TimeOnly(22, 0), back.Jobs[0].Condition.WindowStart);
    }

    [Fact]
    public void JobQueueDocument_ShouldLoadUnknownEnumValuesAsUnknown()
    {
        const string json = """{"jobs":[{"id":"j1","kind":"identifyMusic","recordingId":"r","state":"sleeping","condition":{"kind":"whenOnWifi"},"newField":1}]}""";

        var doc = Deserialize(json, HistoryJsonContext.Default.JobQueueDocument);

        var job = Assert.Single(doc.Jobs);
        Assert.Equal(HistoryJobKind.Unknown, job.Kind);
        Assert.Equal(JobState.Unknown, job.State);
        Assert.Equal(JobConditionKind.Unknown, job.Condition.Kind);
        Assert.NotNull(job.ExtensionData);
    }

    [Fact]
    public void JsonValues_ShouldConvertPrimitivesInvariantly()
    {
        Assert.Equal(JsonValueKind.True, JsonValues.From(true).ValueKind);
        Assert.Equal("1.1", JsonValues.From(1.1).GetRawText());
        Assert.Equal("42", JsonValues.From(42).GetRawText());
        Assert.Equal("\"cpu\"", JsonValues.From("cpu").GetRawText());
        Assert.Equal(JsonValueKind.Null, JsonValues.From(null).ValueKind);
    }
}
