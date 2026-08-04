"""Fix duplicate 'direction' key in Xenova tokenizer.json (breaks Microsoft.ML.Tokenizers)."""
import re

p = "models/lva/embeddings/l1-minilm/tokenizer.json"
raw = open(p, "r", encoding="utf-8").read()
idxs = [m.start() for m in re.finditer(r'"direction": "Right"', raw)]
print("occurrences:", len(idxs))
for i in idxs:
    print(raw[max(0, i - 150):i + 50].replace("\n", " "))
    print("---")
