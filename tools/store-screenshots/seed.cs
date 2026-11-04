#:project ../../apps/VoiceType.Uno/src/VoiceType.Uno.Core/VoiceType.Uno.Core.csproj
#:project ../../libraries/SpeechLib/src/SpeechLib.Providers/SpeechLib.Providers.csproj
#:property TargetFramework=net10.0

// Store screenshots: real recordings for History. Each WAV (SAPI voices, make-audio.ps1) is recognized by the real model with
// the History offline path (OfflineTranscriber, as OfflineRecognitionJob does) and stored with RecordingStore.
// Usage: dotnet run seed.cs -- <sessions dir> <asr models dir> <model folder> <audio dir>
using SpeechLib.AudioStorage;
using SpeechLib.Providers;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;

var (sessions, asrRoot, modelFolder, audioDir) = (args[0], args[1], args[2], args[3]);
var model = InstalledAsrModels.Find(modelFolder, [asrRoot]) ?? throw new InvalidOperationException($"{modelFolder} not found in {asrRoot}");
var store = new RecordingStore(sessions);
if (store.LoadAll().Recordings.Count > 0)
    throw new InvalidOperationException($"{sessions} already has recordings");

var now = new DateTimeOffset(2026, 10, 28, 11, 30, 0, TimeSpan.FromHours(3));
(string File, string Title, DateTimeOffset Started)[] items =
[
    ("voice-note.wav", "Blog post ideas", now.AddDays(-6).AddHours(-2)),
    ("planerka.wav", "Планёрка: задачи на неделю", now.AddDays(-2).AddHours(-1)),
    ("customer-interview.wav", "Customer interview", now.AddDays(-1).AddHours(3)),
    ("product-sync.wav", "Product sync: release 2.0", now.AddHours(-2)),
];

using var recognizer = RecognizerFactory.Create(new RecognizerFactoryOptions
{
    ModelPath = model.Path,
    ExecutionProvider = "cpu",
    Language = "auto",
    UseVad = true,
    StreamingChunkSeconds = model.Family == AsrModelFamilies.ParakeetTdt ? 1.0 : null,
});

foreach (var (file, title, started) in items)
{
    var wav = Path.Combine(audioDir, file);
    var samples = RecordingAudioReader.ReadMono(wav, recognizer.SampleRate);
    var seconds = samples.Length / (double)recognizer.SampleRate;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var result = OfflineTranscriber.Run(recognizer, samples);
    watch.Stop();

    var entry = store.CreateRecording(started);
    store.AddVersion(entry.Id, new TranscriptVersion
    {
        Kind = VersionKinds.Live,
        Model = model.ToModelInfo(),
        Language = new LanguageInfo { Requested = "auto", Detected = result.DetectedLanguage },
        App = new AppInfo { Name = "VoiceType.Uno", Version = "2.0.0", Platform = "win-x64" },
        Stats = new VersionStats { Rtf = Math.Round(watch.Elapsed.TotalSeconds / seconds, 3), ElapsedSec = Math.Round(watch.Elapsed.TotalSeconds, 1) },
    }, result.Document);
    var folder = store.TryGetFolder(entry.Id)!;
    File.Copy(wav, Path.Combine(folder, "audio.wav"));
    store.FinishRecording(entry.Id, started.AddSeconds(seconds), new AudioInfo
    {
        File = "audio.wav", Codec = "pcm16", SampleRate = recognizer.SampleRate, Channels = 1, DurationSec = Math.Round(seconds, 2),
        Bytes = new FileInfo(Path.Combine(folder, "audio.wav")).Length,
    });
    store.Rename(entry.Id, title);
    Console.WriteLine($"{title} ({seconds:F1} s, RTF {watch.Elapsed.TotalSeconds / seconds:F2}, {result.DetectedLanguage}):");
    Console.WriteLine("  " + result.Document.Text);
}
