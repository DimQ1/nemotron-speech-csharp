#if DEBUG
using System.Text.Json;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>
/// DEBUG-only: with <c>VOICETYPE_DATA_ROOT</c> (a throwaway data root) and <c>VOICETYPE_SEED_CHATS=1</c>,
/// fills an empty root with fake recordings and chats for UI checks (layout, streaming, outdated sources).
/// It never runs against the real data root: without <c>VOICETYPE_DATA_ROOT</c> it does nothing.
/// </summary>
internal static class DebugChatSeed
{
    public static void RunIfRequested(IServiceProvider services)
    {
        if (Environment.GetEnvironmentVariable("VOICETYPE_SEED_CHATS") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOICETYPE_DATA_ROOT")))
            return;

        var recordings = services.GetRequiredService<RecordingStore>();
        var chats = services.GetRequiredService<ChatStore>();
        if (recordings.LoadAll().Recordings.Count > 0 || chats.LoadAll().Chats.Count > 0)
            return;

        var now = DateTimeOffset.Now;
        var weekly = Recording(recordings, "Weekly sync", now.AddHours(-30), 754, twoVersions: true);
        var supplier = Recording(recordings, "Supplier call", now.AddHours(-5), 1260, twoVersions: false);
        var maria = Recording(recordings, "Interview Maria", now.AddDays(-4), 2310, twoVersions: false);
        Recording(recordings, "Q4 planning notes with a rather long title that has to be shortened", now.AddDays(-9), 480, twoVersions: false);

        // 1: three recordings, citations, an event, an incomplete answer, an outdated source (Weekly sync v1).
        var multi = chats.Create("Q4 planning across meetings",
            [(weekly, "v1"), (supplier, "v1"), (maria, "v1")]);
        chats.AppendMessage(multi.Id, Msg(ChatRoles.User, "What could move the release date?"));
        chats.AppendMessage(multi.Id, Msg(ChatRoles.Assistant,
            "**Two risks.** The supplier delivery is promised for the 20th but is not confirmed, and the security review is still open.\n\n- Supplier: Maria will confirm by Friday\n- Review: needs two more days",
            Cite(weekly, "v1", 200), Cite(supplier, "v1", 725)));
        chats.AppendMessage(multi.Id, Msg(ChatRoles.System, "Interview Maria added · Gemma now reads 3 recordings"));
        chats.AppendMessage(multi.Id, Msg(ChatRoles.User, "Summarise everything in one paragraph"));
        var cut = Msg(ChatRoles.Assistant, "The team plans the Q4 release for the 24th, the supplier call confirmed the parts, and Maria described the hiring plan, but");
        cut.ExtensionData = new() { ["finish"] = JsonSerializer.SerializeToElement("cancelled") };
        chats.AppendMessage(multi.Id, cut);

        // 2: one recording, up to date.
        var single = chats.Create("Supplier risks", [(supplier, "v1")]);
        chats.AppendMessage(single.Id, Msg(ChatRoles.User, "Which parts are late?"));
        chats.AppendMessage(single.Id, Msg(ChatRoles.Assistant, "Only the **power supplies** are late; the rest arrives on time.", Cite(supplier, "v1", 330)));

        // 3: empty thread (as created by "Ask Gemma").
        chats.Create(null, [(maria, "v1")]);

        // 4: a long thread, for scrolling.
        var longChat = chats.Create("Long conversation", [(weekly, "v2"), (maria, "v1")]);
        for (var i = 1; i <= 14; i++)
        {
            chats.AppendMessage(longChat.Id, Msg(ChatRoles.User, $"Question {i}: what was said about topic {i}?"));
            chats.AppendMessage(longChat.Id, Msg(ChatRoles.Assistant,
                $"Topic {i} came up briefly. The speakers agreed to come back to it next week and to write the decision down.",
                Cite(weekly, "v2", 30.0 * i)));
        }
    }

    /// <summary>
    /// <c>VOICETYPE_START_PAGE</c>: <c>chats</c>, <c>chat:&lt;title part&gt;</c> or <c>recording:&lt;title part&gt;</c>
    /// opens that screen on top of the main page (layout checks).
    /// </summary>
    public static void NavigateIfRequested(Frame frame, IServiceProvider services)
    {
        if (Environment.GetEnvironmentVariable("VOICETYPE_START_PAGE") is not { Length: > 0 } target
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOICETYPE_DATA_ROOT")))
            return;
        if (target == "chats")
        {
            frame.Navigate(typeof(ChatsPage));
        }
        else if (target == "history-settings")
        {
            frame.Navigate(typeof(VoiceType.Uno.Presentation.History.HistorySettingsPage));
        }
        else if (target == "models-speakers")
        {
            frame.Navigate(typeof(ModelsPage), ModelsTab.Speakers);
        }
        else if (target.StartsWith("chats:", StringComparison.Ordinal))
        {
            var part = target[6..];
            var chat = services.GetRequiredService<ChatStore>().LoadAll().Chats
                .FirstOrDefault(c => c.Title.Contains(part, StringComparison.OrdinalIgnoreCase));
            frame.Navigate(typeof(ChatsPage), chat?.Id);
        }
        else if (target.StartsWith("chat:", StringComparison.Ordinal))
        {
            var part = target[5..];
            var chat = services.GetRequiredService<ChatStore>().LoadAll().Chats
                .FirstOrDefault(c => c.Title.Contains(part, StringComparison.OrdinalIgnoreCase));
            if (chat is not null)
                frame.Navigate(typeof(ChatPage), chat.Id);
        }
        else if (target.StartsWith("recording:", StringComparison.Ordinal))
        {
            var part = target[10..];
            var entry = services.GetRequiredService<RecordingStore>().LoadAll().Recordings
                .FirstOrDefault(r => r.Manifest.EffectiveTitle.Contains(part, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
                frame.Navigate(typeof(VoiceType.Uno.Presentation.History.RecordingPage), entry.Id);
        }
    }

    private static ChatMessage Msg(string role, string text, params ChatCitation[] citations) =>
        new() { Role = role, Text = text, Citations = [.. citations] };

    private static ChatCitation Cite(string recordingId, string versionId, double seconds) =>
        new() { RecordingId = recordingId, VersionId = versionId, StartSec = seconds };

    private static string Recording(RecordingStore store, string title, DateTimeOffset started, double seconds, bool twoVersions)
    {
        var entry = store.CreateRecording(started);
        var doc = Transcript(title, seconds);
        store.AddVersion(entry.Id, new TranscriptVersion
        {
            Kind = VersionKinds.Live,
            Model = new ModelInfo { Folder = "parakeet-int4", Title = "Parakeet INT4", CatalogRepoId = "repo/parakeet-int4" },
            Language = new LanguageInfo { Requested = "auto", Detected = "en" },
            App = new VoiceType.Uno.History.AppInfo { Name = "VoiceType.Uno", Version = "1.0", Platform = "win-x64" },
        }, doc);
        if (twoVersions)
        {
            store.AddVersion(entry.Id, new TranscriptVersion
            {
                Kind = VersionKinds.Rerecognized,
                Parent = "v1",
                Model = new ModelInfo { Folder = "parakeet-fp32", Title = "Parakeet FP32", CatalogRepoId = "repo/parakeet-fp32" },
                Language = new LanguageInfo { Requested = "auto", Detected = "en" },
                App = new VoiceType.Uno.History.AppInfo { Name = "VoiceType.Uno", Version = "1.0", Platform = "win-x64" },
            }, doc);
        }
        store.Rename(entry.Id, title);
        // A silent WAV (8 kHz mono) so the player, Reprocess and Detect speakers are enabled in the sandbox.
        var audio = new AudioInfo { File = "audio.wav", Codec = "pcm16", SampleRate = 8000, Channels = 1, DurationSec = seconds };
        if (store.TryGetFolder(entry.Id) is { } folder)
            audio.Bytes = WriteSilentWav(Path.Combine(folder, audio.File), seconds, audio.SampleRate);
        store.FinishRecording(entry.Id, started.AddSeconds(seconds), audio);
        return entry.Id;
    }

    private static long WriteSilentWav(string path, double seconds, int sampleRate)
    {
        var dataBytes = (int)(seconds * sampleRate) * 2;
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        file.SetLength(44 + dataBytes);
        return file.Length;
    }

    private static TranscriptDocument Transcript(string title, double seconds)
    {
        var doc = new TranscriptDocument();
        var count = 14;
        var step = seconds / count;
        for (var i = 0; i < count; i++)
        {
            var text = i == 0
                ? $"This is {title}; we start with the agenda."
                : $"Segment {i + 1} of {title}: the team discusses the release plan, the supplier schedule and the open risks.";
            doc.Segments.Add(new TranscriptSegment
            {
                Index = i,
                Start = i * step,
                End = (i + 1) * step - 0.5,
                Text = text,
            });
        }
        doc.Text = TranscriptDocument.JoinSegments(doc.Segments);
        return doc;
    }
}
#endif
