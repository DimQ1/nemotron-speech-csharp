#!/usr/bin/env bash
# Deep diagnostic: list all X11 windows while app runs, check if it creates one
set -euo pipefail

export DISPLAY=:0
export XDG_RUNTIME_DIR=/run/user/0
export PULSE_SERVER=/mnt/wslg/PulseServer

cd /root/voicetype-uno

echo "=== X11 windows BEFORE app ==="
python3 -c "
import ctypes, ctypes.util
libX11 = ctypes.CDLL(ctypes.util.find_library('X11'))
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
libX11.XQueryTree.restype = ctypes.c_int
libX11.XFetchName.restype = ctypes.c_int

d = libX11.XOpenDisplay(None)
root = libX11.XDefaultRootWindow(d)

# Query window tree
class WindowAttr(ctypes.Structure):
    _fields_ = [
        ('x', ctypes.c_int), ('y', ctypes.c_int),
        ('width', ctypes.c_int), ('height', ctypes.c_int),
    ] * 2  # simplified

children = ctypes.POINTER(ctypes.c_ulong)()
nchildren = ctypes.c_uint()
libX11.XQueryTree(d, root, ctypes.byref(ctypes.c_ulong()), ctypes.byref(ctypes.c_ulong()),
    ctypes.byref(children), ctypes.byref(nchildren))

print(f'Root children count: {nchildren.value}')
for i in range(min(nchildren.value, 20)):
    w = children[i]
    name = ctypes.create_string_buffer(256)
    libX11.XFetchName(d, w, ctypes.byref(name))
    print(f'  Window 0x{w:x} name=\"{name.value.decode() if name.value else \"N/A\"}\"')

libX11.XCloseDisplay(d)
" 2>&1

echo ""
echo "=== Starting VoiceType.Uno in background ==="
# Also capture console output if any
./VoiceType.Uno > /tmp/voicetype-stdout.log 2> /tmp/voicetype-stderr.log &
PID=$!
echo "PID=$PID"

sleep 5

echo "=== App stdout/stderr ==="
echo "---stdout---"
cat /tmp/voicetype-stdout.log 2>/dev/null || echo "(empty)"
echo "---stderr---"
cat /tmp/voicetype-stderr.log 2>/dev/null || echo "(empty)"

echo ""
echo "=== X11 windows AFTER app start (5s) ==="
python3 -c "
import ctypes, ctypes.util
libX11 = ctypes.CDLL(ctypes.util.find_library('X11'))
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
libX11.XQueryTree.restype = ctypes.c_int
libX11.XFetchName.restype = ctypes.c_int

d = libX11.XOpenDisplay(None)
root = libX11.XDefaultRootWindow(d)

children = ctypes.POINTER(ctypes.c_ulong)()
nchildren = ctypes.c_uint()
libX11.XQueryTree(d, root, ctypes.byref(ctypes.c_ulong()), ctypes.byref(ctypes.c_ulong()),
    ctypes.byref(children), ctypes.byref(nchildren))

print(f'Root children count: {nchildren.value}')
for i in range(min(nchildren.value, 20)):
    w = children[i]
    name = ctypes.create_string_buffer(256)
    libX11.XFetchName(d, w, ctypes.byref(name))
    print(f'  Window 0x{w:x} name=\"{name.value.decode() if name.value else \"N/A\"}\"')

libX11.XCloseDisplay(d)
" 2>&1

echo ""
echo "=== App still alive? ==="
cat /proc/$PID/status 2>/dev/null | grep State || echo "process gone"

kill $PID 2>/dev/null || true
wait $PID 2>/dev/null || true
echo "=== DONE ==="
