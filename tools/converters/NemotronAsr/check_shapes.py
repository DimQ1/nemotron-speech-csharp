import json
import onnx

for tag in ["c056", "c112"]:
    p = f"src/build/onnx_models_opset24_fp32_{tag}/genai_config.json"
    c = json.load(open(p))["model"]
    print(f"{tag}: chunk_samples={c['chunk_samples']}, left_context={c['left_context']}, subsampling={c['subsampling_factor']}")

print()
for tag in ["c056", "c112"]:
    m = onnx.load(f"src/build/onnx_models_opset24_fp32_{tag}/encoder.onnx", load_external_data=False)
    for inp in m.graph.input:
        if "cache_last_channel" in inp.name and "len" not in inp.name:
            dims = [d.dim_value for d in inp.type.tensor_type.shape.dim]
            print(f"{tag}: {inp.name} shape={dims}")
            break
    for inp in m.graph.input:
        if inp.name == "audio_signal":
            dims = [d.dim_value for d in inp.type.tensor_type.shape.dim]
            print(f"{tag}: audio_signal shape={dims}")
            break
