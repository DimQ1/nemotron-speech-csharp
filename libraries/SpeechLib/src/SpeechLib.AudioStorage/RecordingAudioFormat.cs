namespace SpeechLib.AudioStorage;

/// <summary>Container/codec of a stored recording.</summary>
public enum RecordingAudioCodec
{
    /// <summary>Ogg Opus (RFC 7845), plays in browsers, VLC, foobar2000, Android, Windows 10+.</summary>
    OggOpus,

    /// <summary>RIFF WAVE, PCM16 mono — lossless, ~115 MB per hour at 16 kHz.</summary>
    Wav,
}

/// <summary>
/// How a recording is stored. <see cref="Standard"/> is the default (Ogg Opus 48 kbps VBR);
/// <see cref="High"/> keeps the exact samples the recognizer saw (WAV PCM16).
/// </summary>
/// <param name="Codec">Container/codec.</param>
/// <param name="BitrateKbps">Opus target bitrate in kbit/s (6..128). Ignored for WAV.</param>
/// <param name="Complexity">Opus encoder complexity 0..10 (10 = best quality, slowest). Ignored for WAV.</param>
/// <param name="Application">Opus application mode. Ignored for WAV.</param>
public sealed record RecordingAudioFormat(
    RecordingAudioCodec Codec,
    int BitrateKbps = 48,
    int Complexity = 10,
    OpusApplicationMode Application = OpusApplicationMode.Audio)
{
    /// <summary>
    /// Default storage: Ogg Opus 48 kbps VBR (~19.5 MB/h incl. container).
    /// Measured with Parakeet TDT INT4 on CV17 en+ru (500 utterances, tools/WerEval --codec):
    /// WER 8.33% vs 8.10% for WAV (+0.23 pp); 32 kbps gave +0.53 pp, 24 kbps +0.79 pp, 16 kbps +1.04 pp.
    /// </summary>
    public static RecordingAudioFormat Standard { get; } = new(RecordingAudioCodec.OggOpus, 48);

    /// <summary>Lossless storage for users who re-recognize a lot: WAV PCM16 (~115 MB/h at 16 kHz).</summary>
    public static RecordingAudioFormat High { get; } = new(RecordingAudioCodec.Wav, 0);

    /// <summary>File extension including the dot: <c>.ogg</c> or <c>.wav</c>.</summary>
    /// <remarks>
    /// <c>.ogg</c> rather than <c>.opus</c>: older Windows players and some Android
    /// file pickers recognize <c>.ogg</c> but not <c>.opus</c>; the content is the same.
    /// </remarks>
    public string FileExtension => Codec == RecordingAudioCodec.Wav ? ".wav" : ".ogg";

    /// <summary>Parses <c>wav</c>, <c>opus</c>, <c>opus:24</c>, <c>opus:24:voip</c>, <c>opus:24:audio:5</c>.</summary>
    public static RecordingAudioFormat Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        var parts = spec.Trim().ToLowerInvariant().Split(':');
        switch (parts[0])
        {
            case "wav":
                return High;
            case "opus":
            case "ogg":
                int kbps = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : Standard.BitrateKbps;
                var app = parts.Length > 2 && parts[2] == "voip" ? OpusApplicationMode.Voip : OpusApplicationMode.Audio;
                int complexity = parts.Length > 3 ? int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) : 10;
                return new RecordingAudioFormat(RecordingAudioCodec.OggOpus, kbps, complexity, app);
            default:
                throw new FormatException($"Unknown audio format '{spec}'. Use wav or opus[:kbps[:audio|voip[:complexity]]].");
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Codec == RecordingAudioCodec.Wav
        ? "wav"
        : $"opus:{BitrateKbps}:{(Application == OpusApplicationMode.Voip ? "voip" : "audio")}:{Complexity}";
}

/// <summary>Opus encoder application (maps to OPUS_APPLICATION_*).</summary>
public enum OpusApplicationMode
{
    /// <summary>Faithful reproduction of the input (best for later re-recognition).</summary>
    Audio,

    /// <summary>Speech-tuned: high-pass filter and intelligibility emphasis.</summary>
    Voip,
}
