#!/usr/bin/env bash
export DISPLAY=:0

echo "=== WSLg diagnostics ==="
dmesg 2>/dev/null | grep -i "wslg\|weston\|rdp" | tail -5 || echo "no dmesg"
echo ""
echo "=== WSLg mount contents ==="
ls -la /mnt/wslg/ 2>/dev/null
echo ""
echo "=== X11 atoms ==="
python3 -c "
import ctypes
libX11 = ctypes.CDLL('libX11.so.6')
libX11.XOpenDisplay.restype = ctypes.c_void_p
d = libX11.XOpenDisplay(None)
if d:
    for name in ['WM_PROTOCOLS', '_NET_WM_NAME', 'WM_NAME']:
        atom = libX11.XInternAtom(d, name.encode(), 0)
        print('  %s: %d' % (name, atom))
    libX11.XCloseDisplay(d)
else:
    print('  XOpenDisplay FAILED')
" 2>&1
echo ""
echo "=== Try running glxgears to test OpenGL ==="
which glxgears 2>/dev/null && glxgears -info 2>&1 | head -3 || echo "glxgears not installed"
echo ""
echo "=== DONE ==="
