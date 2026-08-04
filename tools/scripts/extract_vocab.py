"""Extract WordPiece vocab from HF fast tokenizer.json into vocab.txt format."""
import json

def extract(src, dst):
    with open(src, "r", encoding="utf-8") as f:
        data = json.load(f)
    vocab = data["model"]["vocab"]
    if isinstance(vocab, dict):
        items = sorted(vocab.items(), key=lambda kv: kv[1])
        tokens = [t for t, _ in items]
    else:
        # list of [token, id] or [token, score]
        tokens = [entry[0] for entry in vocab]
    with open(dst, "w", encoding="utf-8") as f:
        for token in tokens:
            f.write(token + "\n")
    print(f"wrote {len(tokens)} tokens -> {dst}")

extract("models/lva/embeddings/l1-minilm/tokenizer.json",
    "models/lva/embeddings/l1-minilm/vocab.txt")
