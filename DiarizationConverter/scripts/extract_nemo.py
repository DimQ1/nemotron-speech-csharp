"""Extract .nemo checkpoint and load with NeMo for ONNX export."""
import tarfile, os, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NEMO_PATH = ROOT / "raw" / "diar_streaming_sortformer_4spk-v2.nemo"
EXTRACT_DIR = ROOT / "raw" / "extracted"

# Step 1: Extract
if not EXTRACT_DIR.exists() or not (EXTRACT_DIR / "model_config.yaml").exists():
    print("Extracting .nemo archive...")
    EXTRACT_DIR.mkdir(parents=True, exist_ok=True)
    with tarfile.open(str(NEMO_PATH), "r:") as t:
        t.extractall(str(EXTRACT_DIR))
    for f in sorted(os.listdir(str(EXTRACT_DIR))):
        size = os.path.getsize(str(EXTRACT_DIR / f)) / (1024 * 1024)
        print(f"  {f}: {size:.1f} MB")
else:
    print("Already extracted.")

# Step 2: Read config
import yaml
with open(EXTRACT_DIR / "model_config.yaml", "r") as f:
    config = yaml.safe_load(f)

print(f"\nModel config keys: {list(config.keys())}")
if "model" in config:
    print(f"Model class: {config.get('target', 'N/A')}")
    model_cfg = config.get("model", {})
    print(f"Model type: {model_cfg.get('cls', 'N/A')}")
    print(f"Sample rate: {model_cfg.get('sample_rate', 'N/A')}")
    print(f"Num speakers: {model_cfg.get('num_spks', 'N/A')}")

print("\nDone extracting. Ready for NeMo load.")
