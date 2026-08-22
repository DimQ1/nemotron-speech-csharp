"""Compare WER between FP32 reference and INT4 variants using local test audio."""
import subprocess, os, re
from pathlib import Path
from jiwer import wer

REPO = Path(r'e:\Work\Dimq1\Audio\nemotron-speech-csharp')
TEST_AUDIO = REPO / 'Test-Audio' / 'sample-0.mp3'

def run_model(model_path, audio_path, ep='cpu'):
    cmd = ['dotnet', 'run', '--project', str(REPO / 'NemotronSpeech/NemotronSpeech.csproj'),
           '-c', 'Release', '-p:GpuArch=CPU', '--no-build', '--',
           model_path, str(audio_path), ep]
    result = subprocess.run(cmd, capture_output=True, text=True, cwd=str(REPO), timeout=180)
    lines = result.stdout.strip().split('\n')
    for i, line in enumerate(lines):
        if line.startswith('===') and i+1 < len(lines):
            return lines[i+1].strip()
    return ''

def clean(text):
    return re.sub(r'\s+', ' ', re.sub(r'[^A-Za-z0-9\' ]', '', text.upper())).strip()

models = {
    'INT4 Symmetric': str(REPO / 'modules/asr/cpu-opset24-int4-new'),
    'INT4 Asymmetric': str(REPO / 'modules/asr/cpu-opset24-int4-asym'),
}

print('=== Getting FP32 reference ===')
ref_text = run_model(str(REPO / 'modules/asr/cpu-opset24-fp32-c056'), TEST_AUDIO)
ref_clean = clean(ref_text)
print(f'Reference (FP32): "{ref_clean}"')

print('\n=== Running INT4 models ===')
for name, path in models.items():
    hyp = run_model(path, TEST_AUDIO)
    hyp_clean = clean(hyp)
    err = wer(ref_clean, hyp_clean)
    match = 'MATCH' if ref_clean == hyp_clean else f'WER={err:.1%}'
    print(f'{name}: "{hyp_clean}" — {match}')
