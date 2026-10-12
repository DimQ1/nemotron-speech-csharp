namespace SpeechLib.AudioStorage.Tests;

public class Unit_RecordingAudioFormatTests
{
    [Theory]
    [InlineData("wav", RecordingAudioCodec.Wav, ".wav")]
    [InlineData("opus", RecordingAudioCodec.OggOpus, ".ogg")]
    [InlineData("opus:24", RecordingAudioCodec.OggOpus, ".ogg")]
    [InlineData("OPUS:16:voip:5", RecordingAudioCodec.OggOpus, ".ogg")]
    public void Parse_ShouldReadCodecAndExtension(string spec, RecordingAudioCodec codec, string extension)
    {
        var format = RecordingAudioFormat.Parse(spec);
        Assert.Equal(codec, format.Codec);
        Assert.Equal(extension, format.FileExtension);
    }

    [Fact]
    public void Parse_ShouldRoundTripToString()
    {
        var format = RecordingAudioFormat.Parse("opus:16:voip:5");
        Assert.Equal(16, format.BitrateKbps);
        Assert.Equal(OpusApplicationMode.Voip, format.Application);
        Assert.Equal(5, format.Complexity);
        Assert.Equal(format, RecordingAudioFormat.Parse(format.ToString()));
        Assert.Equal(RecordingAudioFormat.Standard, RecordingAudioFormat.Parse(RecordingAudioFormat.Standard.ToString()));
    }

    [Fact]
    public void Parse_ShouldRejectUnknownCodec() =>
        Assert.Throws<FormatException>(() => RecordingAudioFormat.Parse("mp3"));
}
