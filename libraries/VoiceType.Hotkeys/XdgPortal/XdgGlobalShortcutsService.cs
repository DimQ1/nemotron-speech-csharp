using Tmds.DBus;

namespace VoiceType.Hotkeys.XdgPortal;

/// <summary>
/// Linux global hotkeys via the XDG GlobalShortcuts portal (D-Bus).
/// Works on Wayland AND X11 when the session runs xdg-desktop-portal ≥ 1.18
/// (GNOME 44+, KDE Plasma 5.27+). The compositor shows a consent dialog once;
/// the binding persists in the portal across restarts.
///
/// Registration flow per shortcut:
///   CreateSession → BindShortcuts → await Request.Response → Watch Activated signal.
/// </summary>
public sealed class XdgGlobalShortcutsService : IGlobalHotkeyService
{
    private const string PortalBusName = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");
    private static int s_tokenCounter;

    private readonly Connection _connection;
    private readonly IGlobalShortcutsPortal _portal;

    private ObjectPath _sessionHandle;
    private IDisposable? _activatedWatch;
    private IDisposable? _deactivatedWatch;
    private int _nextId;
    private bool _disposed;

    /// <summary>Registration id → portal shortcut id (for diagnostics).</summary>
    private readonly Dictionary<int, string> _registrations = new();

    private XdgGlobalShortcutsService(Connection connection, IGlobalShortcutsPortal portal)
    {
        _connection = connection;
        _portal = portal;
    }

    public bool IsAvailable => !_disposed;

    public event Action<int>? HotkeyPressed;

    /// <summary>
    /// Connect to the session bus and verify the portal implements
    /// org.freedesktop.portal.GlobalShortcuts. Returns null when unavailable
    /// (headless session, old portal, no D-Bus) — callers then fall back to
    /// <see cref="NullGlobalHotkeyService"/>.
    /// </summary>
    public static async Task<XdgGlobalShortcutsService?> TryCreateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            var connection = new Connection(Address.Session);
            await connection.ConnectAsync();

            var portal = connection.CreateProxy<IGlobalShortcutsPortal>(PortalBusName, PortalPath);

            // Probe: the interface version property exists only when the backend
            // (GNOME/KDE impl) actually exposes GlobalShortcuts.
            var version = await portal.GetAsync<uint>("version");
            if (version < 1)
            {
                connection.Dispose();
                return null;
            }

            return new XdgGlobalShortcutsService(connection, portal);
        }
        catch
        {
            // No session bus, no portal, or old portal — hotkeys unavailable
            return null;
        }
    }

    public async Task<int> RegisterAsync(string chord, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!HotkeyChord.TryParse(chord, out var parsed) || parsed.PortalModifiers == 0)
            return 0; // portal requires at least one modifier; bare keys are not grabbable

        var session = await EnsureSessionAsync();

        var id = ++_nextId;
        var shortcutId = $"voicetype-{id}";

        var bindHandle = await _portal.BindShortcutsAsync(
            session,
            [(shortcutId, $"VoiceType: {parsed}")],
            parentWindow: "",
            new Dictionary<string, object>
            {
                // Pre-seed the trigger so the compositor can persist the binding
                // without asking the user to press the chord interactively.
                ["shortcuts"] = new (string Id, IDictionary<string, object> Description)[]
                {
                    (shortcutId, new Dictionary<string, object>
                    {
                        ["trigger_description"] = $"{FormatModifiers(parsed)}{parsed.Key}"
                    })
                }
            });

        var accepted = await WaitRequestResponseAsync(bindHandle, cancellationToken);
        if (!accepted)
            return 0;

        await EnsureWatchersAsync();
        _registrations[id] = shortcutId;
        return id;
    }

    public Task UnregisterAllAsync(CancellationToken cancellationToken = default)
    {
        _registrations.Clear();
        // Bindings live in the portal session; dropping the session handle
        // releases them (session closes when the app exits / connection drops).
        _sessionHandle = default;
        return Task.CompletedTask;
    }

    // ── Internals ────────────────────────────────────────────────

    private async Task<ObjectPath> EnsureSessionAsync()
    {
        if (_sessionHandle != default)
            return _sessionHandle;

        var token = NewToken();
        var requestHandle = await _portal.CreateSessionAsync(new Dictionary<string, object>
        {
            ["handle_token"] = token,
            ["session_handle_token"] = token
        });

        // The session handle is returned via the Request.Response signal
        // (results["session_handle"]) — CreateSessionAsync returns the request path.
        // If the response is missing/malformed we propagate 'default' and let
        // BindShortcutsAsync fail loudly instead of guessing a path.
        _sessionHandle = await WaitSessionCreatedAsync(requestHandle, CancellationToken.None);

        return _sessionHandle;
    }

    private async Task EnsureWatchersAsync()
    {
        _activatedWatch ??= await _portal.WatchActivatedAsync(signal =>
        {
            foreach (var (id, shortcutId) in _registrations)
            {
                if (shortcutId == signal.ShortcutId)
                {
                    HotkeyPressed?.Invoke(id);
                    break;
                }
            }
        });

        _deactivatedWatch ??= await _portal.WatchDeactivatedAsync(_ => { });
    }

    private async Task<bool> WaitRequestResponseAsync(ObjectPath requestHandle, CancellationToken ct)
    {
        var request = _connection.CreateProxy<IPortalRequest>(PortalBusName, requestHandle);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watch = await request.WatchResponseAsync(r =>
            completion.TrySetResult(r.Response == 0));
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));

        return await completion.Task;
    }

    private async Task<ObjectPath> WaitSessionCreatedAsync(ObjectPath requestHandle, CancellationToken ct)
    {
        var request = _connection.CreateProxy<IPortalRequest>(PortalBusName, requestHandle);
        var completion = new TaskCompletionSource<ObjectPath>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watch = await request.WatchResponseAsync(r =>
        {
            if (r.Response == 0 &&
                r.Results.TryGetValue("session_handle", out var handle) &&
                handle is string path)
            {
                completion.TrySetResult(new ObjectPath(path));
            }
            else
            {
                completion.TrySetResult(default);
            }
        });
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));

        return await completion.Task;
    }

    private static string NewToken() =>
        $"voicetype{Interlocked.Increment(ref s_tokenCounter)}";

    private static string FormatModifiers(HotkeyChord chord)
    {
        var parts = new List<string>(4);
        if (chord.Ctrl) parts.Add("CTRL");
        if (chord.Shift) parts.Add("SHIFT");
        if (chord.Alt) parts.Add("ALT");
        if (chord.Super) parts.Add("LOGO");
        return string.Concat(parts.Select(p => p + "+"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _activatedWatch?.Dispose();
        _deactivatedWatch?.Dispose();
        _registrations.Clear();
        _connection.Dispose();
        await Task.CompletedTask;
    }
}
