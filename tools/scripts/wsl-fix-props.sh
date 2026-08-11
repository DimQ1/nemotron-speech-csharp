#!/usr/bin/env bash
export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000
mkdir -p /run/user/1000

cd /home/voicetype/voicetype-uno
chmod +x VoiceType.Uno
./VoiceType.Uno >/dev/null 2>/dev/null &
PID=$!
sleep 5

# Find and fix our app window
python3 -c "
import ctypes, struct

libX11 = ctypes.CDLL('libX11.so.6')
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
libX11.XChangeProperty.restype = ctypes.c_int
d = libX11.XOpenDisplay(None)
root = libX11.XDefaultRootWindow(d)

root_r = ctypes.c_ulong(); parent_r = ctypes.c_ulong()
children = ctypes.POINTER(ctypes.c_ulong)(); n = ctypes.c_uint()
libX11.XQueryTree(d, root, ctypes.byref(root_r), ctypes.byref(parent_r),
    ctypes.byref(children), ctypes.byref(n))

# Find our window (size >100x100 and not 8192x8192)
STRING_ATOM = libX11.XInternAtom(d, b'STRING', 0)
WM_CLASS_ATOM = libX11.XInternAtom(d, b'WM_CLASS', 0)
NET_WM_NAME_ATOM = libX11.XInternAtom(d, b'_NET_WM_NAME', 0)
UTF8_ATOM = libX11.XInternAtom(d, b'UTF8_STRING', 0)

for i in range(n.value):
    w = children[i]
    buf = (ctypes.c_ubyte * 200)()
    r = libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
    if not r: continue
    b = bytes(buf)
    ww = struct.unpack_from('i', b, 8)[0]
    wh = struct.unpack_from('i', b, 12)[0]
    
    if ww > 100 and wh > 100 and ww != 8192:
        print('Fixing window 0x%x (%dx%d)' % (w, ww, wh))
        
        # Set WM_CLASS = 'VoiceType.Uno\0VoiceType.Uno'
        wm_class = b'VoiceType.Uno\x00VoiceType.Uno'
        libX11.XChangeProperty(d, w, WM_CLASS_ATOM, STRING_ATOM, 8, 0, wm_class, len(wm_class))
        
        # Set _NET_WM_NAME = 'VoiceType Uno'
        wm_name = b'VoiceType Uno'
        libX11.XChangeProperty(d, w, NET_WM_NAME_ATOM, UTF8_ATOM, 8, 0, wm_name, len(wm_name))
        
        print('Properties set!')

libX11.XFree(children)
libX11.XCloseDisplay(d)
" 2>&1

echo "---"
echo "App PID=$PID, waiting 3 more seconds for RAIL..."
sleep 3

# Check weston log for RAIL registration
echo "=== weston.log (last 5) ==="
tail -5 /mnt/wslg/weston.log

kill $PID 2>/dev/null; wait $PID 2>/dev/null
echo "DONE"
