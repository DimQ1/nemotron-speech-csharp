"""Runtime patch for Olive 0.8 + torch >= 2.12 compatibility.

torch.onnx.export removed the ``fallback`` keyword in torch 2.12, but
Olive 0.8's OnnxConversion pass still calls it with ``dynamo=True,
fallback=True``. Instead of rewriting Olive's function source (fragile),
we wrap ``torch.onnx.export`` itself and drop the unsupported ``fallback``
kwarg before it reaches torch.

Import this module and call :func:`patch_olive_torch_export` before running
the Olive pipeline.
"""

import inspect

_PATCHED = False


def patch_olive_torch_export():
    global _PATCHED
    if _PATCHED:
        return

    import torch

    # Only needed when torch.onnx.export no longer accepts 'fallback'.
    if "fallback" in inspect.signature(torch.onnx.export).parameters:
        return

    original_export = torch.onnx.export

    def export_no_fallback(*args, **kwargs):
        kwargs.pop("fallback", None)
        return original_export(*args, **kwargs)

    export_no_fallback.__wrapped__ = original_export
    torch.onnx.export = export_no_fallback

    # Some call sites bind torch.onnx early via `from torch import onnx`.
    # Patch the already-imported onnx module attribute too, if present.
    try:
        import torch.onnx as tonnx
        tonnx.export = export_no_fallback
    except Exception:
        pass

    _PATCHED = True
    print("[patch_olive_torch_export] dropped unsupported 'fallback' kwarg from torch.onnx.export")
