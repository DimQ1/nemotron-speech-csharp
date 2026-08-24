import ctypes, time

libX11 = ctypes.CDLL("libX11.so.6")
libX11.XOpenDisplay.restype = ctypes.c_void_p
d = libX11.XOpenDisplay(None)

screen = libX11.XDefaultScreen(d)
root = libX11.XDefaultRootWindow(d)
w = libX11.XCreateSimpleWindow(d, root, 100, 100, 400, 300, 1, 0, 0xFFFFFF)
libX11.XStoreName(d, w, b"WSLg Test Window")

WM_CLASS = libX11.XInternAtom(d, b"WM_CLASS", 0)
STRING = libX11.XInternAtom(d, b"STRING", 0)
wmclass_data = b"TestApp\x00TestApp"
libX11.XChangeProperty(d, w, WM_CLASS, STRING, 8, 0, wmclass_data, len(wmclass_data))

libX11.XMapWindow(d, w)
libX11.XFlush(d)
print("Window created and mapped: 0x%x" % w)
print("Window should be visible on your Windows desktop now (8 seconds)...")

time.sleep(8)
libX11.XDestroyWindow(d, w)
libX11.XCloseDisplay(d)
print("Done.")
