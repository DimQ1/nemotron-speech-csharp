#!/usr/bin/env bash
export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000
mkdir -p /run/user/1000

cd /home/voicetype/voicetype-uno
chmod +x VoiceType.Uno
./VoiceType.Uno >/dev/null 2>/dev/null &
PID=$!
sleep 5

# Find the app window and check its properties
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

print('Children: %d' % n.value)

# Check properties on each window  
for i in range(n.value):
    w = children[i]
    buf = (ctypes.c_ubyte * 200)()
    r = libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
    if not r: continue
    b = bytes(buf)
    ww = struct.unpack_from('i', b, 8)[0]
    wh = struct.unpack_from('i', b, 12)[0]
    
    # Only our app window has size > 100x100
    if ww > 100 and wh > 100:
        print('Our window: 0x%x %dx%d' % (w, ww, wh))
        
        # Get WM_CLASS
        hint_type = libX11.XInternAtom(d, b'WM_CLASS', 0)
        actual_type = ctypes.c_ulong(); actual_fmt = ctypes.c_int()
        nitems = ctypes.c_ulong(); bytes_after = ctypes.c_ulong()
        prop = ctypes.c_void_p()
        
        r = libX11.XGetWindowProperty(d, w, hint_type, 0, 256, 0, 0,
            ctypes.byref(actual_type), ctypes.byref(actual_fmt),
            ctypes.byref(nitems), ctypes.byref(bytes_after),
            ctypes.byref(prop))
        if r == 0 and prop:
            data = ctypes.string_at(prop, nitems.value * (actual_fmt.value // 8))
            print('WM_CLASS: %s' % data.decode())
        else:
            print('WM_CLASS: NOT SET')
            
        # Get _NET_WM_NAME  
        hint_type = libX11.XInternAtom(d, b'_NET_WM_NAME', 0)
        r = libX11.XGetWindowProperty(d, w, hint_type, 0, 256, 0, 0,
            ctypes.byref(actual_type), ctypes.byref(actual_fmt),
            ctypes.byref(nitems), ctypes.byref(bytes_after),
            ctypes.byref(prop))
        if r == 0 and prop:
            data = ctypes.string_at(prop, nitems.value * (actual_fmt.value // 8))
            print('_NET_WM_NAME: %s' % data.decode())
        else:
            print('_NET_WM_NAME: NOT SET')

libX11.XFree(children)
libX11.XCloseDisplay(d)
" 2>&1

echo "---"
cat /proc/$PID/status 2>/dev/null | grep State
kill $PID 2>/dev/null; wait $PID 2>/dev/null
echo "DONE"
