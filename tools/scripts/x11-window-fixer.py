#!/usr/bin/env python3
"""X11 window fixer for VoiceType.Uno on Uno Platform Skia/X11.

Uno Platform creates the X11 window via XCreateWindow but never calls
XMapWindow, so the window stays invisible. WSLg (Weston+FreeRDP+RAIL)
requires WM_CLASS to register the window for RDP remote-app integration.

This script monitors the X11 window tree and fixes any new window that looks
like our app (size >200x200, not the 8192x8192 WSLg root overlay). It keeps
running for 20 seconds to handle window recreation by Uno/Skia.
"""
import ctypes, struct, time, sys

libX11 = ctypes.CDLL("libX11.so.6")
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
libX11.XMapWindow.restype = ctypes.c_int
libX11.XRaiseWindow.restype = ctypes.c_int
libX11.XFlush.restype = ctypes.c_int
libX11.XFree.restype = ctypes.c_int

d = libX11.XOpenDisplay(None)
if not d:
    print("Cannot open X11 display", file=sys.stderr)
    sys.exit(1)

STRING     = libX11.XInternAtom(d, b"STRING", 0)
WM_CLASS   = libX11.XInternAtom(d, b"WM_CLASS", 0)
NET_NAME   = libX11.XInternAtom(d, b"_NET_WM_NAME", 0)
UTF8       = libX11.XInternAtom(d, b"UTF8_STRING", 0)
WM_NAME    = libX11.XInternAtom(d, b"WM_NAME", 0)
WM_PROTOCOLS = libX11.XInternAtom(d, b"WM_PROTOCOLS", 0)
WM_DELETE  = libX11.XInternAtom(d, b"WM_DELETE_WINDOW", 0)

ROOT = libX11.XDefaultRootWindow(d)
WMCLASS_DATA = b"VoiceType.Uno\x00VoiceType.Uno"
TITLE = b"VoiceType Uno"
SEEN = set()

def get_windows():
    """Get windows NOT yet seen (for initial detection)."""
    root_r = ctypes.c_ulong(); parent_r = ctypes.c_ulong()
    children = ctypes.POINTER(ctypes.c_ulong)(); n = ctypes.c_uint()
    libX11.XQueryTree(d, ROOT, ctypes.byref(root_r), ctypes.byref(parent_r),
        ctypes.byref(children), ctypes.byref(n))
    result = []
    for i in range(n.value):
        w = children[i]
        if w in SEEN: continue
        buf = (ctypes.c_ubyte * 200)()
        r = libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
        if not r: continue
        b = bytes(buf)
        ww = struct.unpack_from("i", b, 8)[0]
        wh = struct.unpack_from("i", b, 12)[0]
        ms = struct.unpack_from("i", b, 92)[0]
        result.append((w, ww, wh, ms))
    libX11.XFree(children)
    return result

def get_all_windows():
    """Get ALL windows (for continuous remap monitoring)."""
    root_r = ctypes.c_ulong(); parent_r = ctypes.c_ulong()
    children = ctypes.POINTER(ctypes.c_ulong)(); n = ctypes.c_uint()
    libX11.XQueryTree(d, ROOT, ctypes.byref(root_r), ctypes.byref(parent_r),
        ctypes.byref(children), ctypes.byref(n))
    result = []
    for i in range(n.value):
        w = children[i]
        buf = (ctypes.c_ubyte * 200)()
        r = libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
        if not r: continue
        b = bytes(buf)
        ww = struct.unpack_from("i", b, 8)[0]
        wh = struct.unpack_from("i", b, 12)[0]
        ms = struct.unpack_from("i", b, 92)[0]
        result.append((w, ww, wh, ms))
    libX11.XFree(children)
    return result

def fix_window(w, ww, wh, map_window):
    if map_window:
        libX11.XMapWindow(d, w)
    libX11.XChangeProperty(d, w, WM_CLASS, STRING, 8, 0, WMCLASS_DATA, len(WMCLASS_DATA))
    libX11.XChangeProperty(d, w, NET_NAME, UTF8, 8, 0, TITLE, len(TITLE))
    libX11.XChangeProperty(d, w, WM_NAME, STRING, 8, 0, TITLE, len(TITLE))
    atom_atom = libX11.XInternAtom(d, b"ATOM", 0)
    del_atoms = (ctypes.c_ulong * 1)(WM_DELETE)
    libX11.XChangeProperty(d, w, WM_PROTOCOLS, atom_atom, 32, 0, del_atoms, 1)
    libX11.XRaiseWindow(d, w)
    libX11.XFlush(d)
    action = "Mapped" if map_window else "Configured"
    print(f"[{time.strftime('%H:%M:%S')}] {action} 0x{w:x} ({ww}x{wh})", flush=True)

print(f"[{time.strftime('%H:%M:%S')}] Fixer started, monitoring for VoiceType.Uno...", flush=True)

DEADLINE = time.time() + 20
FIXED_WINDOWS = set()

while time.time() < DEADLINE:
    windows = get_all_windows()
    for w, ww, wh, ms in windows:
        if ww > 200 and wh > 200 and ww != 8192:
            if ms == 0 or w not in FIXED_WINDOWS:
                fix_window(w, ww, wh, ms == 0)
                FIXED_WINDOWS.add(w)
    time.sleep(0.5)

libX11.XCloseDisplay(d)
print(f"[{time.strftime('%H:%M:%S')}] Fixer done", flush=True)
