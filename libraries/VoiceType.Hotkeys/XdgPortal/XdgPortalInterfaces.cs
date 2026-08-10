using Tmds.DBus;

namespace VoiceType.Hotkeys.XdgPortal;

// ── D-Bus contracts for the XDG GlobalShortcuts portal ─────────────────────
// Spec: https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html
// Implemented by xdg-desktop-portal >= 1.18 (GNOME 44+/KDE Plasma 5.27+ sessions).

[DBusInterface("org.freedesktop.portal.GlobalShortcuts")]
public interface IGlobalShortcutsPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
    Task<ObjectPath> BindShortcutsAsync(ObjectPath sessionHandle,
        (string Id, string Description)[] shortcuts,
        string parentWindow,
        IDictionary<string, object> options);
    Task<(uint Response, IDictionary<string, object> Results)> ListShortcutsAsync(
        ObjectPath sessionHandle, IDictionary<string, object> options);
    Task<IDisposable> WatchActivatedAsync(
        Action<(ObjectPath SessionHandle, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options)> handler);
    Task<IDisposable> WatchDeactivatedAsync(
        Action<(ObjectPath SessionHandle, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options)> handler);
    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.freedesktop.portal.Request")]
public interface IPortalRequest : IDBusObject
{
    Task CloseAsync();
    Task<IDisposable> WatchResponseAsync(
        Action<(uint Response, IDictionary<string, object> Results)> handler);
}
