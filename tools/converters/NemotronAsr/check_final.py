import onnx

for tag in ["fp32_c056", "fp32_c112", "int4_c056", "int4_c112"]:
    m = onnx.load(f"src/build/onnx_models_opset24_{tag}/encoder.onnx", load_external_data=False)
    ops = {}
    for n in m.graph.node:
        ops[n.op_type] = ops.get(n.op_type, 0) + 1
    os_ = [(o.domain or "ai.onnx", o.version) for o in m.opset_import]
    print(f"{tag}: opset={os_}, Attention={'Attention' in ops}, "
          f"Swish={'Swish' in ops}, MatMulNBits={ops.get('MatMulNBits', 0)}, "
          f"Sigmoid={ops.get('Sigmoid', 0)}")
