namespace VoiceType.Uno.Services.Jobs;

/// <summary>
/// Whether a live recording is running — the "idle" input of the job scheduler
/// (<c>When idle</c> jobs wait, <c>History.AllowJobsDuringRecording = false</c> holds every job).
/// <see cref="Changed"/> may be raised on any thread.
/// </summary>
public interface IRecordingActivity
{
    bool IsLiveRecording { get; }

    event Action? Changed;
}

/// <summary>
/// Settable <see cref="IRecordingActivity"/>: the recording owner (MainViewModel /
/// RecognitionService) calls <see cref="Report"/> when a session starts and stops.
/// </summary>
public sealed class RecordingActivity : IRecordingActivity
{
    private volatile bool _isLive;

    public bool IsLiveRecording => _isLive;

    public event Action? Changed;

    public void Report(bool isLiveRecording)
    {
        if (_isLive == isLiveRecording)
            return;
        _isLive = isLiveRecording;
        Changed?.Invoke();
    }
}
