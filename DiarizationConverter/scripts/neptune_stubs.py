"""
Precise stubs for NVIDIA GPU packages to allow NeMo import on CPU-only Windows.

This creates proper Python packages (not just FakeModule instances) that
can handle import machinery (find_spec, __path__, etc.)
"""

import sys
import types
from pathlib import Path

# ── Windows CPU signal patches ────────────────────────────────────────
import signal
if not hasattr(signal, "SIGKILL"):
    signal.SIGKILL = signal.SIGTERM
    signal.SIGSTOP = signal.SIGTERM

def _create_mock_module(name: str, doc: str = "") -> types.ModuleType:
    """Create a proper mock module that supports importlib machinery."""
    if name in sys.modules:
        return sys.modules[name]

    mod = types.ModuleType(name)
    mod.__doc__ = doc or f"Mock module for {name} (CPU-only stub)"
    mod.__file__ = f"<stub:{name}>"
    mod.__path__ = []  # For packages

    # Create a proper __spec__ to pass importlib.util.find_spec checks
    import importlib.machinery
    loader = importlib.machinery.SourceFileLoader(name, f"<stub:{name}>")
    mod.__spec__ = importlib.machinery.ModuleSpec(name, loader, origin=f"<stub:{name}>")
    mod.__spec__.submodule_search_locations = []

    # Make attribute access return self (for sub-modules)
    class _RecursiveMock:
        def __init__(self, mod_name):
            self._mod_name = mod_name
        def __getattr__(self, attr):
            full = f"{self._mod_name}.{attr}"
            if full not in sys.modules:
                sub = _create_mock_module(full)
                sys.modules[full] = sub
            return sys.modules[full]
        def __call__(self, *args, **kwargs):
            return self
        def __repr__(self):
            return f"<Mock:{self._mod_name}>"
        def __iter__(self):
            return iter([])
        def __bool__(self):
            return False
        def __eq__(self, other):
            return False
        def __ne__(self, other):
            return True

    mock = _RecursiveMock(name)
    
    # Set common magic attributes as strings
    mod.__version__ = "99.0.0"
    mod.__author__ = "stub"
    
    # Make the module itself act as a recursive mock for unknown attrs
    mod.__class__ = type("MockModule", (types.ModuleType,), {
        "__getattr__": lambda s, a: (
            "99.0.0" if a == "__version__" 
            else getattr(mock, a, mock.__getattr__(a))
        ),
    })

    sys.modules[name] = mod
    return mod


# ── Stub GPU-specific packages that block NeMo import on CPU ──────────

STUB_LIST = [
    # NVIDIA GPU/accelerator packages
    "apex",
    "apex.transformer",
    "apex.transformer.enums",
    "apex.transformer.tensor_parallel",
    "apex.transformer.pipeline_parallel",
    "megatron",
    "megatron.core",
    "megatron.core.tensor_parallel",
    "megatron.core.pipeline_parallel",
    "megatron.core.transformer",
    "megatron.core.utils",
    "transformer_engine",
    "nvidia_resiliency_ext",
    "nvidia.distributed",
    # One-logger
    "nv_one_logger",
    "nv_one_logger.api",
    "nv_one_logger.api.config",
]

for name in STUB_LIST:
    _create_mock_module(name)

print(f"Stubbed {len(STUB_LIST)} GPU-specific packages.")
