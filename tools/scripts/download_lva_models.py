"""Download core ONNX models for the LVA pipeline (VAD, L1, L3)."""
import os
from huggingface_hub import snapshot_download

DEST = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "..", "build", "lva-models"))

MODELS = [
    ("onnx-community/silero-vad", ["onnx/model.onnx"], "vad"),
    ("Xenova/paraphrase-multilingual-MiniLM-L12-v2",
     ["onnx/model_int8.onnx", "tokenizer.json", "config.json"], "l1-minilm"),
    ("gpahal/bge-m3-onnx-int8",
     ["onnx/model_int8.onnx", "tokenizer.json", "sentencepiece.bpe.model",
      "config.json", "special_tokens_map.json", "tokenizer_config.json"], "l3-bgem3"),
]

for repo, patterns, sub in MODELS:
    target = os.path.join(DEST, sub)
    os.makedirs(target, exist_ok=True)
    print(f"=== {repo} -> {target}")
    snapshot_download(repo, allow_patterns=patterns, local_dir=target)

print("done")
