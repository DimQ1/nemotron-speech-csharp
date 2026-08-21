"""Complete inventory of bool-related nodes in Nemotron encoder for WebGPU graph capture."""
import onnx
from collections import defaultdict

enc = onnx.load(r'modules/asr/webgpu-int4/encoder.onnx')

# Build graph
producers = {}
consumers = defaultdict(list)
for n in enc.graph.node:
    for o in n.output:
        producers[o] = n
    for i in n.input:
        consumers[i].append(n)

tensor_types = {}
for vi in enc.graph.value_info:
    t = vi.type.tensor_type.elem_type if vi.type.tensor_type else 0
    tensor_types[vi.name] = t
for vi in enc.graph.input:
    t = vi.type.tensor_type.elem_type if vi.type.tensor_type else 0
    tensor_types[vi.name] = t
for vi in enc.graph.output:
    t = vi.type.tensor_type.elem_type if vi.type.tensor_type else 0
    tensor_types[vi.name] = t

TYPE_NAMES = {0:'?', 1:'f32', 2:'u8', 3:'i8', 4:'u16', 5:'i16', 6:'i32', 7:'i64',
              9:'bool', 10:'f16', 11:'f64', 12:'u32', 13:'u64'}

# ==========================================
# 1. ALL LESS NODES - trace to end
# ==========================================
print("=" * 70)
print("STEP 1: All Less nodes and their full chains")
print("=" * 70)

for node in enc.graph.node:
    if node.op_type != 'Less':
        continue
    
    idx = list(enc.graph.node).index(node)
    print(f"\n>>> [{idx}] Less '{node.name}'")
    print(f"    inputs: {list(node.input)}")
    
    # Trace forward
    out = node.output[0]
    for depth in range(15):
        if out not in consumers:
            print(f"    -> END (no consumer: {out})")
            break
        c = consumers[out][0]
        ci = list(enc.graph.node).index(c)
        
        # Get output type
        out_type = tensor_types.get(c.output[0], 0) if c.output else 0
        
        # Cast attributes
        cast_to = ''
        if c.op_type == 'Cast':
            for a in c.attribute:
                if a.name == 'to':
                    cast_to = f" -> {TYPE_NAMES.get(a.i, str(a.i))}"
        
        print(f"    [{ci:4d}] {c.op_type:12s} {c.name[:45]} {cast_to}")
        
        if c.op_type == 'Slice':
            print(f"           *** SLICE *** data type will be changed by our transform")
            break
        
        out = c.output[0] if c.output else ''

# ==========================================
# 2. ALL AND NODES
# ==========================================
print("\n" + "=" * 70)
print("STEP 2: All And nodes")
print("=" * 70)

for node in enc.graph.node:
    if node.op_type != 'And':
        continue
    idx = list(enc.graph.node).index(node)
    print(f"\n>>> [{idx}] And '{node.name}'")
    print(f"    inputs: {list(node.input)}")
    for inp in node.input:
        if inp in producers:
            p = producers[inp]
            pi = list(enc.graph.node).index(p)
            print(f"      <- [{pi}] {p.op_type} '{p.name}'")
    print(f"    output: {list(node.output)}")
    out = node.output[0]
    if out in consumers:
        for c in consumers[out]:
            ci = list(enc.graph.node).index(c)
            print(f"      -> [{ci}] {c.op_type} '{c.name}'")

# ==========================================
# 3. ALL NOT NODES
# ==========================================
print("\n" + "=" * 70)
print("STEP 3: All Not nodes")
print("=" * 70)

for node in enc.graph.node:
    if node.op_type != 'Not':
        continue
    idx = list(enc.graph.node).index(node)
    print(f"\n>>> [{idx}] Not '{node.name}'")
    print(f"    inputs: {list(node.input)}")
    for inp in node.input:
        if inp in producers:
            p = producers[inp]
            pi = list(enc.graph.node).index(p)
            print(f"      <- [{pi}] {p.op_type} '{p.name}'")
    print(f"    output: {list(node.output)}")
    out = node.output[0]
    if out in consumers:
        for c in consumers[out]:
            ci = list(enc.graph.node).index(c)
            print(f"      -> [{ci}] {c.op_type} '{c.name}'")

# ==========================================
# 4. ALL CAST NODES - bool related
# ==========================================
print("\n" + "=" * 70)
print("STEP 4: All Cast nodes")
print("=" * 70)

bool_ops = {'bool': 9, 'float': 1, 'int32': 6, 'int64': 7}

for node in enc.graph.node:
    if node.op_type != 'Cast':
        continue
    idx = list(enc.graph.node).index(node)
    cast_to = 0
    for a in node.attribute:
        if a.name == 'to':
            cast_to = a.i
    if cast_to not in (1, 6, 7, 9):  # skip non-bool-related casts
        continue
    
    print(f"\n>>> [{idx}] Cast '{node.name}' to={TYPE_NAMES.get(cast_to, str(cast_to))}")
    print(f"    inputs: {list(node.input)}")
    for inp in node.input:
        if inp in producers:
            p = producers[inp]
            pi = list(enc.graph.node).index(p)
            print(f"      <- [{pi}] {p.op_type} '{p.name}'")
    print(f"    output: {list(node.output)}")
    out = node.output[0]
    if out in consumers:
        for c in consumers[out]:
            ci = list(enc.graph.node).index(c)
            print(f"      -> [{ci}] {c.op_type} '{c.name}'")
