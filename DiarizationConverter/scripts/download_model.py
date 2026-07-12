"""
Download Sortformer model from HuggingFace Hub.

Source: nvidia/diar_streaming_sortformer_4spk-v2
Output: raw/ directory with PyTorch checkpoint and config.
"""

import os
import sys
from pathlib import Path
from huggingface_hub import snapshot_download

REPO_ID = "nvidia/diar_streaming_sortformer_4spk-v2"
OUTPUT_DIR = Path(__file__).resolve().parent.parent / "raw"


def main():
    print(f"Downloading {REPO_ID} from HuggingFace Hub...")
    print(f"Output: {OUTPUT_DIR}")

    # Try with HF token if available (for gated models)
    token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGINGFACE_TOKEN")

    snapshot_download(
        repo_id=REPO_ID,
        local_dir=str(OUTPUT_DIR),
        token=token,
        ignore_patterns=["*.md", ".gitattributes"],
    )

    # List downloaded files
    files = list(OUTPUT_DIR.glob("*"))
    total_size = sum(f.stat().st_size for f in files) / (1024 * 1024)
    print(f"\nDownloaded {len(files)} files ({total_size:.1f} MB):")
    for f in sorted(files):
        size_mb = f.stat().st_size / (1024 * 1024)
        print(f"  {f.name} ({size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
