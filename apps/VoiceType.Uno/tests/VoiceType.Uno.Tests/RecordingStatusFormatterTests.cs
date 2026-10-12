using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class RecordingStatusFormatterTests
{
    [Theory]
    [InlineData(false, false, false, false, RecordingPhase.Idle)]
    [InlineData(false, false, true, false, RecordingPhase.Recording)]
    [InlineData(false, false, true, true, RecordingPhase.Muted)]
    [InlineData(false, false, false, true, RecordingPhase.Idle)]
    [InlineData(false, true, true, false, RecordingPhase.LoadingModel)]
    [InlineData(true, true, false, false, RecordingPhase.DownloadingModel)]
    public void GetPhase_ShouldPreferModelPreparationOverRecordingFlags(
        bool downloading, bool loading, bool recording, bool muted, RecordingPhase expected)
    {
        Assert.Equal(expected, RecordingStatusFormatter.GetPhase(downloading, loading, recording, muted));
    }

    [Theory]
    [InlineData(RecordingPhase.Idle, RecordGlyph.Microphone)]
    [InlineData(RecordingPhase.Recording, RecordGlyph.Stop)]
    [InlineData(RecordingPhase.Muted, RecordGlyph.Microphone)]
    [InlineData(RecordingPhase.LoadingModel, RecordGlyph.Microphone)]
    [InlineData(RecordingPhase.DownloadingModel, RecordGlyph.Microphone)]
    public void GetGlyph_ShouldShowStopOnlyWhileListening(RecordingPhase phase, RecordGlyph expected)
    {
        Assert.Equal(expected, RecordingStatusFormatter.GetGlyph(phase));
    }

    [Theory]
    [InlineData(RecordingPhase.Idle, "Tap to start")]
    [InlineData(RecordingPhase.Recording, "Tap to stop")]
    [InlineData(RecordingPhase.Muted, "Muted · tap to resume")]
    [InlineData(RecordingPhase.LoadingModel, "Loading model…")]
    [InlineData(RecordingPhase.DownloadingModel, "Downloading model…")]
    public void GetCaption_ShouldDescribeWhatATapDoes(RecordingPhase phase, string expected)
    {
        Assert.Equal(expected, RecordingStatusFormatter.GetCaption(phase));
    }

    [Theory]
    [InlineData(RecordingPhase.Idle, "Ready")]
    [InlineData(RecordingPhase.Recording, "Rec 12:34")]
    [InlineData(RecordingPhase.Muted, "Muted 12:34")]
    [InlineData(RecordingPhase.LoadingModel, "Loading model…")]
    [InlineData(RecordingPhase.DownloadingModel, "Downloading…")]
    public void GetPillText_ShouldShowTheSessionTimeOnlyWhileRecording(RecordingPhase phase, string expected)
    {
        var elapsed = new TimeSpan(0, 12, 34);

        Assert.Equal(expected, RecordingStatusFormatter.GetPillText(phase, elapsed));
    }

    [Theory]
    [InlineData(RecordingPhase.Idle, "Start recording")]
    [InlineData(RecordingPhase.Recording, "Stop recording")]
    [InlineData(RecordingPhase.Muted, "Resume recording")]
    public void GetButtonName_ShouldNameTheTapAction(RecordingPhase phase, string expected)
    {
        Assert.Equal(expected, RecordingStatusFormatter.GetButtonName(phase));
    }

    [Theory]
    [InlineData(false, "Recording")]
    [InlineData(true, "Muted")]
    public void GetNoticeTitle_ShouldReflectMuteState(bool muted, string expected)
    {
        Assert.Equal(expected, RecordingStatusFormatter.GetNoticeTitle(muted));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(754, "12:34")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(37230, "10:20:30")]
    [InlineData(-5, "00:00")]
    public void FormatElapsed_ShouldSwitchToHoursAfterAnHour(int seconds, string expected)
    {
        Assert.Equal(expected, RecordingStatusFormatter.FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }
}
