using Tmds.DBus;

namespace VoiceType.Hotkeys.StatusNotifier;

// ── D-Bus contracts for the StatusNotifierItem (AppIndicator) spec ─────────
// Spec: https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/
// Used by GNOME (AppIndicator extension), KDE Plasma, XFCE, etc.

[DBusInterface("org.kde.StatusNotifierItem")]
public interface IStatusNotifierItem : IDBusObject
{
    Task<string> GetTitleAsync();
    Task<string> GetIconNameAsync();
    Task<string> GetStatusAsync();
    Task<int> GetItemIsMenuAsync();
    Task<ObjectPath> GetMenuAsync();

    Task ActivateAsync(int x, int y);
    Task SecondaryActivateAsync(int x, int y);
    Task ScrollAsync(int delta, string orientation);
}

[DBusInterface("org.kde.StatusNotifierWatcher")]
public interface IStatusNotifierWatcher : IDBusObject
{
    Task RegisterStatusNotifierItemAsync(string serviceOrPath);
    Task<T> GetAsync<T>(string propertyName);
}

/// <summary>
/// A registered StatusNotifierItem (tray icon) handle. Dispose to remove.
/// </summary>
public interface ITrayIconHandle : IDisposable
{
    /// <summary>Update the tooltip/title shown for the tray icon.</summary>
    void SetTooltip(string tooltip);

    /// <summary>Update the status (Passive / Active / NeedsAttention).</summary>
    void SetStatus(TrayStatus status);

    /// <summary>Raised when the user activates (left-clicks) the tray icon.</summary>
    event Action? Activated;
}

public enum TrayStatus
{
    Passive,
    Active,
    NeedsAttention
}

/// <summary>
/// Linux tray icon via the StatusNotifierItem D-Bus protocol.
/// Registers with org.kde.StatusNotifierWatcher and serves properties
/// (Title, IconName, Status) from a dedicated object path.
/// </summary>
public sealed class StatusNotifierTrayIcon : ITrayIconHandle
{
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";

    private readonly Connection _connection;
    private readonly string _serviceName;
    private readonly ObjectPath _objectPath;
    private readonly TrayItemObject _itemObject;
    private bool _disposed;

    private StatusNotifierTrayIcon(Connection connection, string serviceName, ObjectPath objectPath, TrayItemObject itemObject)
    {
        _connection = connection;
        _serviceName = serviceName;
        _objectPath = objectPath;
        _itemObject = itemObject;
    }

    public event Action? Activated
    {
        add => _itemObject.Activated += value;
        remove => _itemObject.Activated -= value;
    }

    /// <summary>
    /// Registers a tray icon. Returns null when the StatusNotifierWatcher is not
    /// available (no tray on the desktop environment).
    /// </summary>
    public static async Task<StatusNotifierTrayIcon?> TryCreateAsync(
        string serviceName,
        string title,
        string iconName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = Connection.Session;
            var objectPath = new ObjectPath("/StatusNotifierItem");
            var itemObject = new TrayItemObject(title, iconName);

            await connection.RegisterObjectAsync(itemObject).ConfigureAwait(false);
            await connection.RegisterServiceAsync(serviceName).ConfigureAwait(false);

            var watcher = connection.CreateProxy<IStatusNotifierWatcher>(WatcherService, WatcherPath);
            await watcher.RegisterStatusNotifierItemAsync(serviceName)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            return new StatusNotifierTrayIcon(connection, serviceName, objectPath, itemObject);
        }
        catch
        {
            return null;
        }
    }

    public void SetTooltip(string tooltip) => _itemObject.SetTitle(tooltip);
    public void SetStatus(TrayStatus status) => _itemObject.SetStatus(status);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ = _connection.UnregisterServiceAsync(_serviceName);
        _connection.UnregisterObject(_objectPath);
        _connection.Dispose();
    }

    // ── Served object ─────────────────────────────────────────────────────

    private sealed class TrayItemObject : IStatusNotifierItem
    {
        private string _title;
        private string _status = "Active";

        public TrayItemObject(string title, string iconName)
        {
            _title = title;
            ObjectPath = new ObjectPath("/StatusNotifierItem");
            IconName = iconName;
        }

        public ObjectPath ObjectPath { get; }
        public string IconName { get; }

        public event Action? Activated;

        public void SetTitle(string title) => _title = title;
        public void SetStatus(TrayStatus status) =>
            _status = status switch
            {
                TrayStatus.Active => "Active",
                TrayStatus.NeedsAttention => "NeedsAttention",
                _ => "Passive"
            };

        public Task<string> GetTitleAsync() => Task.FromResult(_title);
        public Task<string> GetIconNameAsync() => Task.FromResult(IconName);
        public Task<string> GetStatusAsync() => Task.FromResult(_status);
        public Task<int> GetItemIsMenuAsync() => Task.FromResult(0);
        public Task<ObjectPath> GetMenuAsync() => Task.FromResult(new ObjectPath("/MenuBar"));

        public Task ActivateAsync(int x, int y)
        {
            Activated?.Invoke();
            return Task.CompletedTask;
        }

        public Task SecondaryActivateAsync(int x, int y) => Task.CompletedTask;
        public Task ScrollAsync(int delta, string orientation) => Task.CompletedTask;
    }
}
