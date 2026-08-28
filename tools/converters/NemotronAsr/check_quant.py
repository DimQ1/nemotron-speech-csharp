import inspect
from olive.passes.onnx.kquant_quantization import OnnxKQuantQuantization

src = inspect.getsource(OnnxKQuantQuantization)
import re
# find _config class params
i = src.find("_default_config")
print(src[i:i+1500] if i >= 0 else src[:1500])
