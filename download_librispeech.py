"""Extract 5 LibriSpeech test-clean samples for ASR testing."""
import tarfile, subprocess, shutil
from pathlib import Path

OUT = Path('Test-Audio/librispeech')
OUT.mkdir(parents=True, exist_ok=True)

WANTED = {
    '61-70968-0000': 'he could be absolutely being five centuries ago of having a talk with him and actually heard him speak',
    '61-70968-0002': 'were they ever a great power these parthians',
    '61-70968-0004': 'at the end of about six months he had mastered the art of the language',
    '61-70968-0006': 'some of our craft were furious at the terms of the armistice',
    '61-70968-0008': 'here and there along the streets he met his acquaintances and kept stepping aside to let the chairs pass',
}

TAR = OUT / 'test-clean.tar.gz'
if not TAR.exists():
    import urllib.request
    url = 'http://www.openslr.org/resources/12/test-clean.tar.gz'
    print(f'Downloading {url}...')
    urllib.request.urlretrieve(url, TAR)

with tarfile.open(TAR, 'r:gz') as tar:
    for m in tar.getmembers():
        for key, text in WANTED.items():
            if key in m.name and m.name.endswith('.flac'):
                tar.extract(m, OUT)
                src = OUT / m.name
                dst = OUT / (key.replace('-', '_') + '.flac')
                if src != dst:
                    src.rename(dst)
                wav = OUT / (key.replace('-', '_') + '.wav')
                subprocess.run(['ffmpeg', '-y', '-i', str(dst), '-ar', '16000', '-ac', '1', str(wav)],
                              capture_output=True)
                try:
                    dst.unlink()
                except PermissionError:
                    pass
                (OUT / (key.replace('-', '_') + '.txt')).write_text(text)
                dur = subprocess.check_output(
                    ['ffprobe', '-v', 'error', '-show_entries', 'format=duration',
                     '-of', 'default=noprint_wrappers=1:nokey=1', str(wav)], text=True).strip()
                print(f'  {wav.name}: {float(dur):.1f}s — "{text}"')

shutil.rmtree(OUT / 'LibriSpeech', ignore_errors=True)
TAR.unlink()
print(f'\nDone! {len(WANTED)} files in {OUT}')
