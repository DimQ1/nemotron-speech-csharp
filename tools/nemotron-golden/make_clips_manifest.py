"""Builds clips.json (the exact list of golden clips) and vad_scenarios.json.

usage: python make_clips_manifest.py <TestAudioRoot> <goldenAudioDir> <outDir>

Roots used in the manifests:
  TestAudio : the repository's gitignored Test-Audio folder (relative paths like cv17/en/0001.wav)
  golden    : <golden>/audio (generated wavs, truth fixtures, decoded sample-0.mp3) - NOT committed, regenerate with tools/gen_audio.cs
"""
import hashlib, json, os, sys, wave, struct

test_audio, golden_audio, out_dir = sys.argv[1:4]


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def wav_info(path):
    with open(path, 'rb') as f:
        b = f.read()
    pos = 12
    fmt = bits = None
    while pos + 8 <= len(b):
        cid = b[pos:pos + 4]
        size = struct.unpack_from('<i', b, pos + 4)[0]
        if cid == b'fmt ':
            fmt = struct.unpack_from('<H', b, pos + 8)[0]
            bits = struct.unpack_from('<h', b, pos + 22)[0]
            rate = struct.unpack_from('<i', b, pos + 12)[0]
        if cid == b'data':
            n = min(size, len(b) - pos - 8) // (bits // 8)
            return n, fmt, bits, rate, hashlib.sha256(b[pos + 8:pos + 8 + min(size, len(b) - pos - 8)]).hexdigest()
        pos += 8 + size + (size & 1)
    raise ValueError(path)


clips = []
seen_sha = {}


def add(cid, root, rel, lang, lang_name, kind, sets, note=None):
    base = test_audio if root == 'TestAudio' else golden_audio
    path = os.path.join(base, rel)
    n, fmt, bits, rate, pcm_sha = wav_info(path)
    assert rate == 16000, (path, rate)
    file_sha = sha(path)
    entry = {
        'id': cid, 'root': root, 'path': rel.replace('\\', '/'), 'lang': lang, 'langName': lang_name, 'kind': kind,
        'sets': sets, 'samples': n, 'seconds': round(n / 16000, 3), 'wavFormat': 'pcm16' if fmt == 1 else 'float32',
        'sha256File': file_sha if root == 'TestAudio' else None, 'sha256Pcm': pcm_sha,
    }
    if note:
        entry['note'] = note
    clips.append(entry)
    seen_sha.setdefault(file_sha, cid)


EN, RU, AUTO = ('0', 'en'), ('11', 'ru'), ('101', 'auto')
ALL = ['primary']
C112 = ['primary', 'c112']
FP32 = ['primary', 'c112', 'fp32']

# ---- Common Voice 17 test clips (en/ru 0001-0040) ------------------------------------------------------------------------------
for lang, (lid, lname) in (('en', EN), ('ru', RU)):
    for i in range(1, 41):
        sets = ALL + (['c112'] if i <= 10 else []) + (['fp32'] if i <= 3 else [])
        note = None
        if lang == 'en' and i == 24:
            note = 'peak -55 dBFS (quietest CV clip); raw recognizer, no AGC'
            sets = ALL + ['c112', 'fp32']
        if lang == 'en' and i == 3:
            note = 'GenAI INT4 c056 emits no token at all for this clip (A2)'
        add(f'cv17-{lang}-{i:04d}', 'TestAudio', f'cv17/{lang}/{i:04d}.wav', lid, lname, 'cv17', sets, note)

# a few extra naturally quiet clips outside 0001-0040
add('cv17-en-0086', 'TestAudio', 'cv17/en/0086.wav', *EN, 'cv17-quiet', ALL, 'peak -22.8 dBFS')
add('cv17-ru-0069', 'TestAudio', 'cv17/ru/0069.wav', *RU, 'cv17-quiet', ALL, 'peak -22.5 dBFS')

# ---- cv17-hard (en): 0007 0008 0009 0014 0024 0034 are byte-identical to cv17/en/<same number> (flagged), 0051 lies outside 0001-0040 --------
HARD = ('0007', '0008', '0009', '0014', '0024', '0034', '0051')
for c in clips:
    if c['id'].startswith('cv17-en-') and c['id'][-4:] in HARD:
        c['hard'] = True
        c['note'] = ((c.get('note') + '; ') if c.get('note') else '') + 'also in Test-Audio/cv17-hard/en (byte identical)'
        assert hashlib.sha256(open(os.path.join(test_audio, 'cv17-hard', 'en', c['id'][-4:] + '.wav'), 'rb').read()).hexdigest() == c['sha256File']
        if 'c112' not in c['sets']:
            c['sets'].append('c112')
add('cv17hard-en-0051', 'TestAudio', 'cv17-hard/en/0051.wav', *EN, 'cv17-hard', ALL + ['c112'], 'peak -26.7 dBFS; identical to cv17/en/0051')
# ---- long / short / TTS ---------------------------------------------------------------------------------------------------------
add('cv17long-en-0001', 'TestAudio', 'cv17-long/en/long-0001.wav', *EN, 'cv17-long', C112, '98.9 s, 24 CV sentences back to back (peak -1.2 dBFS)')
add('cv17longnorm-en-0001', 'TestAudio', 'cv17-long-norm/en/long-0001.wav', *EN, 'cv17-long', ALL, 'same as cv17-long, peak-normalized to 0 dBFS')
add('cv17short-en-0001', 'TestAudio', 'cv17-short/en/short-0001.wav', *EN, 'cv17-short', C112, '16.5 s')
add('tts-long-en-0001', 'TestAudio', 'tts-long/en/tts-0001.wav', *EN, 'tts', FP32, '58.3 s English TTS')
add('tts-ru-ru-0001', 'TestAudio', 'tts-ru/ru/ru-0001.wav', *RU, 'tts', C112, '17.4 s Russian TTS (pauses)')
add('tts-ru-ru-0002', 'TestAudio', 'tts-ru/ru/ru-0002.wav', *RU, 'tts', FP32, '8.4 s Russian TTS')

# ---- sample-0.mp3 decoded with the repo's AudioUtils.LoadFile(path, 16000) (Media Foundation + StreamingResampler) -----------------------
add('sample-0-mp3', 'golden', 'sample-0.f32.wav', *EN, 'mp3', C112,
    'Test-Audio/sample-0.mp3 decoded by SpeechLib.Audio.AudioUtils.LoadFile(path,16000) into float32 (IEEE-float wav); Media Foundation adds ~70 ms of decoder delay vs libsndfile (161786 samples)')

# ---- TTS fixtures with word truth (A0, SAPI) -----------------------------------------------------------------------------------
for lang, (lid, lname) in (('en', EN), ('ru', RU)):
    for i in range(1, 11):
        sets = ALL + (['c112'] if i <= 3 else [])
        add(f'truth-{lang}-{i:04d}', 'golden', f'truth/{lang}/{i:04d}.wav', lid, lname, 'truth-tts', sets, 'SAPI TTS fixture of the word-timing study (not in the repo)')

add('gen-vad-a4-exact', 'golden', 'gen-vad-a4-exact.f32.wav', *EN, 'generated', ALL + ['c112'],
    'tools/gen_a4_exact.cs: A4 exp1 input bit-for-bit (System.Random(7) + Box-Muller sigma 0.002 float32 room tone): lead 1 s, cv17/en/0001, 10 s, cv17/en/0005, 2 s tail')

# ---- generated clips (tools/gen_audio.cs) ---------------------------------------------------------------------------------------
gen = json.load(open(os.path.join(golden_audio, 'generated_audio.json'), encoding='utf-8'))
gen_lang = {
    'gen-concat-en-gaps': EN, 'gen-concat-ru-gaps': RU, 'gen-concat-mix-auto': AUTO, 'gen-short-0p3s-en': EN, 'gen-exact-8960-en': EN,
    'gen-exact-17920-en': EN, 'gen-exact-53760-en': EN, 'gen-exact-53761-en': EN, 'gen-exact-53760-ru': RU, 'gen-exact-53759-en': EN,
    'gen-silence-room-5s': EN, 'gen-silence-zeros-2s': EN, 'gen-quiet-30dB-en0001': EN, 'gen-quiet-30dB-ru0001': RU,
}
for g in gen['clips']:
    cid = g['id']
    if cid.startswith('gen-vad-'):
        lang = EN
        sets = ALL + (['c112'] if cid in ('gen-vad-a4-10s', 'gen-vad-pause-2s') else [])
    else:
        lang = gen_lang[cid]
        sets = ALL + ['c112']
        if cid in ('gen-short-0p3s-en', 'gen-exact-53761-en', 'gen-silence-room-5s', 'gen-concat-mix-auto'):
            sets = FP32
    add(cid, 'golden', g['file'], *lang, 'generated', sets, g['recipe'])

json.dump({'schema': 1, 'note': 'ids, language ids (LanguageMapper numeric lang_id), audio location and membership in the variant sets (primary = int4-c056, c112 = int4-c112, fp32 = fp32-c056 and fp32-c112)', 'clips': clips},
          open(os.path.join(out_dir, 'clips.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(len(clips), 'clips;', {s: sum(1 for c in clips if s in c['sets']) for s in ('primary', 'c112', 'fp32')},
      'total seconds primary', round(sum(c['seconds'] for c in clips), 1))
dups = {}
for c in clips:
    pass

# ---- VAD scenarios ------------------------------------------------------------------------------------------------------------------
def sc(cid, lang, feeds, root='golden', path=None, note=None):
    d = {'id': cid, 'root': root, 'path': path or f'{cid}.wav', 'lang': lang[0], 'langName': lang[1], 'feeds': feeds}
    if note:
        d['note'] = note
    return d

scenarios = [
    sc('gen-vad-a4-exact', EN, [0, 800], path='gen-vad-a4-exact.f32.wav', note="A4's exact exp1 input (float32, System.Random(7)): c056 dropped chunks 18-30 in A4's logs"),
    sc('gen-vad-a4-10s', EN, [0, 800, 3200], note="A4 exp1 recipe (10 s pause): 0 = chunk-aligned feed (A4's S3), 800 = live-like 50 ms (A4's S1)"),
    sc('gen-vad-pause-2s', EN, [0, 800], note='2 s pause: fewer than 6 silent c056 chunks (and 3 silent c112 chunks 1.12 s each = 3.36 s needs 3), see the dropped list'),
    sc('gen-vad-gap-4chunks', EN, [0, 800]),
    sc('gen-vad-gap-5chunks', EN, [0, 800]),
    sc('gen-vad-gap-6chunks', EN, [0, 800]),
    sc('gen-vad-gap-7chunks', EN, [0, 800]),
    sc('gen-silence-room-5s', EN, [0, 800], note='pure room tone: the VAD drops from the 6th (c056) / 3rd (c112) chunk'),
    sc('gen-silence-zeros-2s', EN, [0]),
    sc('cv17-en-0001', EN, [0], root='TestAudio', path='cv17/en/0001.wav', note='speech only: nothing may be dropped'),
    sc('cv17-ru-0001', RU, [0], root='TestAudio', path='cv17/ru/0001.wav', note='speech only: nothing may be dropped'),
    sc('cv17-en-0003', EN, [0], root='TestAudio', path='cv17/en/0003.wav', note='clip with no GenAI tokens'),
    sc('gen-concat-en-gaps', EN, [0, 800], note='gaps of 1, 2, 3 s between sentences'),
    sc('tts-ru-ru-0001', RU, [0], root='TestAudio', path='tts-ru/ru/ru-0001.wav', note='2 s pauses inside digital silence'),
]
json.dump({'schema': 1, 'scenarios': scenarios}, open(os.path.join(out_dir, 'vad_scenarios.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(len(scenarios), 'vad scenarios')
