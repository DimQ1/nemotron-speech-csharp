"""Download core ONNX models for the LVA pipeline (VAD, L1, L3)."""
import os
from huggingface_hub import snapshot_download

MODELS_ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "..", "models", "lva"))

MODELS = [
        ("onnx-community/silero-vad", ["onnx/model.onnx"], os.path.join("vad", "silero")),
    ("Xenova/paraphrase-multilingual-MiniLM-L12-v2",
         ["onnx/model_int8.onnx", "tokenizer.json", "config.json"], os.path.join("embeddings", "l1-minilm")),
    ("gpahal/bge-m3-onnx-int8",
        ["model_quantized.onnx", "tokenizer.json", "sentencepiece.bpe.model",
            "config.json", "special_tokens_map.json", "tokenizer_config.json"], os.path.join("embeddings", "l3-bgem3")),
]

for repo, patterns, sub in MODELS:
    target = os.path.join(MODELS_ROOT, sub)
    os.makedirs(target, exist_ok=True)
    print(f"=== {repo} -> {target}")
    snapshot_download(repo, allow_patterns=patterns, local_dir=target)

print("done")
