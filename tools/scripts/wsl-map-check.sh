#!/usr/bin/env bash
export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/0 PULSE_SERVER=/mnt/wslg/PulseServer
cd /root/voicetype-uno
./VoiceType.Uno >/dev/null 2>/dev/null &
PID=$!
sleep 6

python3 -c "
import ctypes
libX11 = ctypes.CDLL('libX11.so.6')
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
d = libX11.XOpenDisplay(None)
root = libX11.XDefaultRootWindow(d)
print(f'Root window: 0x{root:x}')

children = (ctypes.c_ulong * 50)()
nchildren = ctypes.c_uint()
root_ret = ctypes.c_ulong()
parent_ret = ctypes.c_ulong()
libX11.XQueryTree(d, root, ctypes.byref(root_ret), ctypes.byref(parent_ret), ctypes.byref(children), ctypes.byref(nchildren))
print(f'Children: {nchildren.value}')

for i in range(nchildren.value):
    w = children[i]
    buf = (ctypes.c_ubyte * 100)()
    libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
    map_state = int.from_bytes(buf[76:80], 'little')
    w_val = int.from_bytes(buf[8:12], 'little')
    h_val = int.from_bytes(buf[12:16], 'little')
    name = ctypes.create_string_buffer(256)
    libX11.XFetchName(d, w, ctypes.byref(name))
    nm = name.value.decode() if name.value else 'N/A'
    print(f'0x{w:x} {w_val}x{h_val} map={map_state} \"{nm}\"')

libX11.XCloseDisplay(d)
" 2>&1

echo "=== STATE ==="
cat /proc/$PID/status 2>/dev/null | grep -E "State|Threads"
kill $PID 2>/dev/null; wait $PID 2>/dev/null
echo "DONE"
