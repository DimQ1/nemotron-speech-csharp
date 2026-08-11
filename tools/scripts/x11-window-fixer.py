#!/usr/bin/env python3
"""X11 window fixer: detect new VoiceType.Uno window, map it, set WM_CLASS."""
import ctypes, struct, time, sys

libX11 = ctypes.CDLL("libX11.so.6")
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
libX11.XMapWindow.restype = ctypes.c_int
libX11.XFlush.restype = ctypes.c_int

def get_window_ids(d, root):
    root_r = ctypes.c_ulong(); parent_r = ctypes.c_ulong()
    children = ctypes.POINTER(ctypes.c_ulong)(); n = ctypes.c_uint()
    libX11.XQueryTree(d, root, ctypes.byref(root_r), ctypes.byref(parent_r),
        ctypes.byref(children), ctypes.byref(n))
    ids = {children[i] for i in range(n.value)}
    libX11.XFree(children)
    return ids

d = libX11.XOpenDisplay(None)
if not d:
    print("Cannot open display", file=sys.stderr)
    sys.exit(1)
root = libX11.XDefaultRootWindow(d)

# Snapshot existing windows
before = get_window_ids(d, root)

# Wait for VoiceType.Uno to create its window
print("Waiting for VoiceType.Uno window...", flush=True)
for attempt in range(60):  # 30 seconds max
    time.sleep(0.5)
    after = get_window_ids(d, root)
    new = after - before
    for w in new:
        buf = (ctypes.c_ubyte * 200)()
        r = libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
        if not r: continue
        b = bytes(buf)
        ww = struct.unpack_from("i", b, 8)[0]
        wh = struct.unpack_from("i", b, 12)[0]
        if ww > 200 and wh > 200:
            print(f"Found window 0x{w:x} ({ww}x{wh})")

            # Map it
            libX11.XMapWindow(d, w)

            # Set WM_CLASS
            STRING = libX11.XInternAtom(d, b"STRING", 0)
            WM_CLASS = libX11.XInternAtom(d, b"WM_CLASS", 0)
            wmclass = b"VoiceType.Uno\x00VoiceType.Uno"
            libX11.XChangeProperty(d, w, WM_CLASS, STRING, 8, 0, wmclass, len(wmclass))

            # Set window title
            NET_NAME = libX11.XInternAtom(d, b"_NET_WM_NAME", 0)
            UTF8 = libX11.XInternAtom(d, b"UTF8_STRING", 0)
            name = b"VoiceType Uno"
            libX11.XChangeProperty(d, w, NET_NAME, UTF8, 8, 0, name, len(name))

            libX11.XFlush(d)
            print("Window mapped and properties set!")
            libX11.XCloseDisplay(d)
            sys.exit(0)

print("Timeout waiting for VoiceType.Uno window", file=sys.stderr)
libX11.XCloseDisplay(d)
sys.exit(1)
