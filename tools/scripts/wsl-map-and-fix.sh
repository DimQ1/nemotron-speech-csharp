#!/usr/bin/env bash
export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000
mkdir -p /run/user/1000
cd /home/voicetype/voicetype-uno
chmod +x VoiceType.Uno
./VoiceType.Uno >/dev/null 2>/dev/null &
PID=$!
sleep 5

# MAP our app window and set app-id properties
python3 -c "
import ctypes, struct

libX11 = ctypes.CDLL('libX11.so.6')
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
d = libX11.XOpenDisplay(None)
root = libX11.XDefaultRootWindow(d)

root_r = ctypes.c_ulong(); parent_r = ctypes.c_ulong()
children = ctypes.POINTER(ctypes.c_ulong)(); n = ctypes.c_uint()
libX11.XQueryTree(d, root, ctypes.byref(root_r), ctypes.byref(parent_r),
    ctypes.byref(children), ctypes.byref(n))

for i in range(n.value):
    w = children[i]
    buf = (ctypes.c_ubyte * 200)()
    r = libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
    if not r: continue
    b = bytes(buf)
    ww = struct.unpack_from('i', b, 8)[0]
    wh = struct.unpack_from('i', b, 12)[0]

    if ww > 100 and wh > 100 and ww != 8192:
        print('App window: 0x%x (%dx%d)' % (w, ww, wh))

        # CRITICAL: Map the window (make it visible)
        libX11.XMapWindow.restype = ctypes.c_int
        r = libX11.XMapWindow(d, w)
        print('XMapWindow result: %d' % r)
        
        # Raise to top
        libX11.XRaiseWindow(d, w)
        
        # Set WM_CLASS for Wayland app_id mapping via XWayland
        STRING = libX11.XInternAtom(d, b'STRING', 0)
        WM_CLASS = libX11.XInternAtom(d, b'WM_CLASS', 0)
        wm_class = b'VoiceType.Uno\x00VoiceType.Uno'
        libX11.XChangeProperty(d, w, WM_CLASS, STRING, 8, 0, wm_class, len(wm_class))
        
        # Set _NET_WM_NAME and WM_NAME
        UTF8 = libX11.XInternAtom(d, b'UTF8_STRING', 0)
        NET_NAME = libX11.XInternAtom(d, b'_NET_WM_NAME', 0)
        WM_NAME = libX11.XInternAtom(d, b'WM_NAME', 0)
        name = b'VoiceType Uno'
        libX11.XChangeProperty(d, w, NET_NAME, UTF8, 8, 0, name, len(name))
        libX11.XChangeProperty(d, w, WM_NAME, STRING, 8, 0, name, len(name))
        
        # Flush to make changes take effect
        libX11.XFlush(d)
        print('Mapped and properties set!')

libX11.XFree(children)
libX11.XCloseDisplay(d)
" 2>&1

echo "---"
sleep 3
echo "=== Weston RAIL log ==="
grep -i "rail\|appid\|0x2\|VoiceType" /mnt/wslg/weston.log | tail -5
echo "=== App state ==="
cat /proc/$PID/status 2>/dev/null | grep State
kill $PID 2>/dev/null; wait $PID 2>/dev/null
echo "DONE"
