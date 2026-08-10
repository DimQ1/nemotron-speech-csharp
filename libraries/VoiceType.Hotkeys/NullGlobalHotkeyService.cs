namespace VoiceType.Hotkeys;

/// <summary>
/// Null Object for environments without global-hotkey capability
/// (unsupported compositor, headless session, tests). Lets ViewModels
/// depend on <see cref="IGlobalHotkeyService"/> unconditionally.
/// </summary>
public sealed class NullGlobalHotkeyService : IGlobalHotkeyService
{
#pragma warning disable CS0067 // Contract member; raised by real backends only
    public event Action<int>? HotkeyPressed;
#pragma warning restore CS0067

    public bool IsAvailable => false;

    public Task<int> RegisterAsync(string chord, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task UnregisterAllAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
