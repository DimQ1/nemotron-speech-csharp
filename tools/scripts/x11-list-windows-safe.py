import ctypes, struct

libX11 = ctypes.CDLL("libX11.so.6")
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
d = libX11.XOpenDisplay(None)
root = libX11.XDefaultRootWindow(d)

root_r = ctypes.c_ulong()
parent_r = ctypes.c_ulong()
children = ctypes.POINTER(ctypes.c_ulong)()
n = ctypes.c_uint()

libX11.XQueryTree(d, root, ctypes.byref(root_r), ctypes.byref(parent_r),
    ctypes.byref(children), ctypes.byref(n))

MAP_NAMES = {0: "UNMAPPED", 1: "UNVIEWABLE", 2: "VIEWABLE"}
print("Top-level windows: %d" % n.value)
for i in range(n.value):
    w = children[i]
    buf = (ctypes.c_ubyte * 200)()
    r = libX11.XGetWindowAttributes(d, w, ctypes.cast(buf, ctypes.c_void_p))
    if r:
        b = bytes(buf)
        ww = struct.unpack_from("i", b, 8)[0]
        wh = struct.unpack_from("i", b, 12)[0]
        ms = struct.unpack_from("i", b, 64)[0]
        mn = MAP_NAMES.get(ms, "?")
        print("  0x%x %dx%d map=%d (%s)" % (w, ww, wh, ms, mn))

libX11.XFree(children)
libX11.XCloseDisplay(d)
