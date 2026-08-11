#!/usr/bin/env bash
# Check window attributes - is it mapped? what size?
set -euo pipefail

export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/0 PULSE_SERVER=/mnt/wslg/PulseServer

cd /root/voicetype-uno
./VoiceType.Uno >/dev/null 2>/dev/null &
PID=$!
sleep 5

echo "=== Inspect NEW window 0x600002 ==="
python3 -c "
import ctypes
libX11 = ctypes.CDLL('libX11.so.6')
libX11.XOpenDisplay.restype = ctypes.c_void_p
d = libX11.XOpenDisplay(None)

# XGetWindowAttributes
class XWindowAttributes(ctypes.Structure):
    _fields_ = [
        ('x', ctypes.c_int), ('y', ctypes.c_int),
        ('width', ctypes.c_int), ('height', ctypes.c_int),
        ('border_width', ctypes.c_int),
        ('depth', ctypes.c_int),
        ('visual', ctypes.c_void_p),
        ('root', ctypes.c_ulong),
        ('class', ctypes.c_int),
        ('bit_gravity', ctypes.c_int),
        ('win_gravity', ctypes.c_int),
        ('backing_store', ctypes.c_int),
        ('backing_planes', ctypes.c_ulong),
        ('backing_pixel', ctypes.c_ulong),
        ('save_under', ctypes.c_int),
        ('colormap', ctypes.c_ulong),
        ('map_installed', ctypes.c_int),
        ('map_state', ctypes.c_int),
        ('all_event_masks', ctypes.c_long),
        ('your_event_mask', ctypes.c_long),
        ('do_not_propagate_mask', ctypes.c_long),
        ('override_redirect', ctypes.c_int),
        ('screen', ctypes.c_void_p),
    ]

libX11.XGetWindowAttributes.restype = ctypes.c_int
attrs = XWindowAttributes()
libX11.XGetWindowAttributes(d, 0x600002, ctypes.byref(attrs))

MAP_STATES = {0: 'IsUnmapped', 1: 'IsUnviewable', 2: 'IsViewable'}
print(f'Size: {attrs.width}x{attrs.height} at ({attrs.x},{attrs.y})')
print(f'Map state: {MAP_STATES.get(attrs.map_state, str(attrs.map_state))}')
print(f'Override redirect: {attrs.override_redirect}')
print(f'Depth: {attrs.depth}')

# Get window name
name = ctypes.create_string_buffer(256)
libX11.XFetchName(d, 0x600002, ctypes.byref(name))
print(f'Title: \"{name.value.decode() if name.value else \"(empty)\"}\"')

# Check WM hints
libX11.XCloseDisplay(d)
" 2>&1

echo ""
echo "=== Check WSLg status ==="
ls /mnt/wslg/.X11-unix/ 2>/dev/null || echo "no .X11-unix in wslg"
cat /mnt/wslg/version 2>/dev/null || echo "no version file"

kill $PID 2>/dev/null; wait $PID 2>/dev/null || true
echo "=== DONE ==="
