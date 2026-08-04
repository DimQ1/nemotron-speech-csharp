"""Inspect ONNX model signatures for LVA models."""
import onnx

MODELS = [
    ("vad", "models/lva/vad/silero/onnx/model.onnx"),
    ("minilm", "models/lva/embeddings/l1-minilm/onnx/model_int8.onnx"),
    ("bgem3", "models/lva/embeddings/l3-bgem3/model_quantized.onnx"),
]

for name, path in MODELS:
    m = onnx.load(path, load_external_data=False)
    print("==", name)
    print(" inputs:", [(i.name, [d.dim_value or d.dim_param for d in i.type.tensor_type.shape.dim])
                        for i in m.graph.input])
    print(" outputs:", [(o.name, [d.dim_value or d.dim_param for d in o.type.tensor_type.shape.dim])
                         for o in m.graph.output])
