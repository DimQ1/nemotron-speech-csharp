# VoiceType.Hotkeys

Cross-platform global hotkey registration for the VoiceType apps.

| Backend | Platform | Session | Status |
|---|---|---|---|
| `XdgGlobalShortcutsService` | Linux | Wayland **and** X11 (via xdg-desktop-portal ≥ 1.18) | ✅ Implemented |
| Win32 `RegisterHotKey` | Windows | any | ⬜ Planned |
| `NullGlobalHotkeyService` | any | unsupported / headless / tests | ✅ Null Object |

## Design

- One contract: `IGlobalHotkeyService` (async, `IAsyncDisposable`, `IsAvailable` probe).
- **Backend selection happens once in the app's composition root** — not inside ViewModels:

```csharp
services.AddSingleton<IGlobalHotkeyService>(_ =>
    OperatingSystem.IsLinux()
        ? await XdgGlobalShortcutsService.TryCreateAsync() ?? new NullGlobalHotkeyService()
        : new NullGlobalHotkeyService());
```

- Chords are parsed once (`HotkeyChord.TryParse("Ctrl+Shift+Space")`) and translated
  per-backend (portal modifier bits; future X11 keysyms / Win32 MOD_*).
- Linux backend talks to `org.freedesktop.portal.GlobalShortcuts` over the session
  D-Bus (Tmds.DBus). Flow: `CreateSession` → `BindShortcuts` (with a pre-seeded
  `trigger_description` so the binding persists without interactive capture) →
  `Request.Response` → `Activated` signal → `HotkeyPressed`.

## Portal availability

Requires xdg-desktop-portal ≥ 1.18 with a backend that implements GlobalShortcuts:
GNOME 44+, KDE Plasma 5.27+ (shortcuts configured in System Settings). On older or
minimal compositors `TryCreateAsync` returns null → the app reports hotkeys as
unavailable instead of failing silently. For pure X11 sessions without the portal,
an `XGrabKey` backend can be added behind the same interface.

## Why a separate project

Global hotkeys are app-independent infrastructure: same contract will serve
VoiceType.Uno, the Windows WinUI app (future Win32 backend replacing its local
GlobalHotkeyService), and any CLI/tray tooling. Keeping it out of the app projects
avoids the WPF↔WinUI service-duplication problem documented in the tech-debt audit.
