import inspect
from olive.passes.onnx.quantization import OnnxMatMul4Quantizer

src = inspect.getsource(OnnxMatMul4Quantizer)
import re
# find _config class params
i = src.find("_default_config")
print(src[i:i+1500] if i >= 0 else src[:1500])
