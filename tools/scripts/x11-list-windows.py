import ctypes

libX11 = ctypes.CDLL("libX11.so.6")
libX11.XOpenDisplay.restype = ctypes.c_void_p
libX11.XDefaultRootWindow.restype = ctypes.c_ulong
d = libX11.XOpenDisplay(None)
root = libX11.XDefaultRootWindow(d)

root_return = ctypes.c_ulong()
parent_return = ctypes.c_ulong()
children_ptr = ctypes.POINTER(ctypes.c_ulong)()
nchildren = ctypes.c_uint()

libX11.XQueryTree(d, root, ctypes.byref(root_return), ctypes.byref(parent_return),
    ctypes.byref(children_ptr), ctypes.byref(nchildren))

MAP_NAMES = {0: "UNMAPPED", 1: "UNVIEWABLE", 2: "VIEWABLE"}
print(f"Top-level windows: {nchildren.value}")
for i in range(nchildren.value):
    w = children_ptr[i]
    attrs = (ctypes.c_int * 24)()
    ret = libX11.XGetWindowAttributes(d, w, ctypes.byref(attrs))
    if ret:
        ww = attrs[2]
        wh = attrs[3]
        map_state = attrs[16]
        name = ctypes.create_string_buffer(256)
        libX11.XFetchName(d, w, ctypes.byref(name))
        nm = name.value.decode() if name.value else "(no title)"
        map_name = MAP_NAMES.get(map_state, "?")
        print(f"  0x{w:x} {ww}x{wh} map={map_state} ({map_name}) [{nm}]")

libX11.XFree(children_ptr)
libX11.XCloseDisplay(d)
