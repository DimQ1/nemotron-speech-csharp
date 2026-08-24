"""Quantize FP32 encoder with different block_size and accuracy_level, then compare WER."""
import onnx, subprocess, re, os, shutil, time
from pathlib import Path
from onnxruntime.quantization.matmul_nbits_quantizer import MatMulNBitsQuantizer
from jiwer import wer

REPO = Path(r'e:\Work\Dimq1\Audio\nemotron-speech-csharp')
FP32 = REPO / 'modules/asr/cpu-opset24-fp32-c056/encoder.onnx'
TEST_DIR = REPO / 'Test-Audio/librispeech'

# Quantization variants to test
VARIANTS = {
    'b32_a4':  dict(block_size=32, accuracy_level=4),
    'b64_a4':  dict(block_size=64, accuracy_level=4),
    'b128_a4': dict(block_size=128, accuracy_level=4),
    'b32_a0':  dict(block_size=32, accuracy_level=0),
    'b64_a0':  dict(block_size=64, accuracy_level=0),
}

def run_model(model_dir, audio_path):
    cmd = ['dotnet', 'run', '--project', str(REPO / 'NemotronSpeech/NemotronSpeech.csproj'),
           '-c', 'Release', '-p:GpuArch=CPU', '--no-build', '--',
           str(model_dir), str(audio_path), 'cpu']
    r = subprocess.run(cmd, capture_output=True, text=True, cwd=str(REPO), timeout=180)
    lines = r.stdout.strip().split('\n')
    for i, line in enumerate(lines):
        if line.startswith('===') and i+1 < len(lines):
            return lines[i+1].strip()
    return ''

def clean(text):
    return re.sub(r'\s+', ' ', re.sub(r'[^A-Za-z0-9\' ]', '', text.upper())).strip()

# Step 1: Quantize all variants
model_dirs = {}
print('=== Quantizing variants ===')
for name, params in VARIANTS.items():
    out_dir = REPO / f'modules/asr/cpu-int4-{name}'
    enc = out_dir / 'encoder.onnx'
    if enc.exists():
        print(f'  SKIP {name} (exists)')
    else:
        out_dir.mkdir(parents=True, exist_ok=True)
        print(f'  {name}: block_size={params["block_size"]}, accuracy_level={params["accuracy_level"]}...')
        t0 = time.time()
        m = onnx.load(str(FP32))
        q = MatMulNBitsQuantizer(m, is_symmetric=True, **params)
        q.process()
        onnx.save_model(q.model.model, str(enc), save_as_external_data=True,
                       all_tensors_to_one_file=True, location='encoder.onnx.data')
        sz = (out_dir / 'encoder.onnx.data').stat().st_size / 1e6
        print(f'    -> {sz:.1f} MB ({time.time()-t0:.0f}s)')
    # Copy support files
    for f in (REPO / 'modules/asr/cpu-opset24-fp32-c056').iterdir():
        if f.name not in ('encoder.onnx', 'encoder.onnx.data'):
            shutil.copy2(f, out_dir / f.name)
    model_dirs[name] = out_dir

# Step 2: Run WER comparison
print('\n=== WER Comparison (5 files, FP32 reference) ===')
test_files = sorted(TEST_DIR.glob('*.wav'))
# Get FP32 reference first
ref_transcripts = {}
for wav in test_files:
    ref = run_model(REPO / 'modules/asr/cpu-opset24-fp32-c056', wav)
    ref_transcripts[wav.stem] = clean(ref)

# Now test all variants
print(f'\n{"Variant":<12} {"Size":>8} {"WER":>8} {"Time":>8}')
print('-' * 42)
for name, mdir in model_dirs.items():
    data_file = mdir / 'encoder.onnx.data'
    size_mb = data_file.stat().st_size / 1e6 if data_file.exists() else 0
    t0 = time.time()
    all_ref, all_hyp = [], []
    for wav in test_files:
        hyp = run_model(mdir, wav)
        all_ref.append(ref_transcripts[wav.stem])
        all_hyp.append(clean(hyp))
    w = wer(' '.join(all_ref), ' '.join(all_hyp))
    print(f'{name:<12} {size_mb:>7.0f}MB {w:>7.1%} {time.time()-t0:>7.0f}s')
