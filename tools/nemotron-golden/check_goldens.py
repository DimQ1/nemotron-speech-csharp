"""Consistency checks of the golden JSONs + the index builder.

usage: python check_goldens.py <goldenNemotronDir> <A2oracleOutDir or ''> [<index.json out>]

 * every JSON: text == concatenated pieces, tokenCount == number of ids, ids in range, chunk indexes consecutive,
   aligned vs ragged3200 vs irregular identical (the harness already computed it; re-verified here), buffer-consistency flag
 * overlap with the earlier A2 oracle JSONs (same GenAI 0.16.0, same models): per-chunk ids must be identical
 * writes index.json: per variant/clip tokens, text, sha256 of the id stream (compact fingerprint for parity tests)
"""
import glob, hashlib, json, os, sys

root = sys.argv[1]
a2dir = sys.argv[2] if len(sys.argv) > 2 else ''
index_out = sys.argv[3] if len(sys.argv) > 3 else os.path.join(root, 'index.json')

A2_MAP = {  # A2 oracle file stem -> clip id
    'en0001': 'cv17-en-0001', 'en0002': 'cv17-en-0002', 'en0003': 'cv17-en-0003', 'ru0001': 'cv17-ru-0001', 'ru0002': 'cv17-ru-0002',
    'ru0003': 'cv17-ru-0003', 'ttsen1': 'tts-long-en-0001', 'ttsru1': 'tts-ru-ru-0001', 'ttsru2': 'tts-ru-ru-0002', 'longen1': 'cv17long-en-0001',
}

problems = []
index = {}
n_files = 0
for variant_dir in sorted(d for d in glob.glob(os.path.join(root, '*')) if os.path.isdir(d) and os.path.basename(d) not in ('vad', 'checks')):
    variant = os.path.basename(variant_dir)
    entries = {}
    for path in sorted(glob.glob(os.path.join(variant_dir, '*.json'))):
        n_files += 1
        d = json.load(open(path, encoding='utf-8'))
        cid = d['clip']
        where = f'{variant}/{cid}'
        all_ids = [i for c in d['chunks'] for i in c['ids']]
        if d['tokenCount'] != len(all_ids):
            problems.append(f'{where}: tokenCount {d["tokenCount"]} != {len(all_ids)} ids')
        if any(i < 0 or i >= 13087 for i in all_ids):
            problems.append(f'{where}: id out of range / blank emitted')
        text = ''.join(p for c in d['chunks'] for p in c['pieces'])
        if text != d['text']:
            problems.append(f'{where}: text != concatenated pieces')
        if not d.get('allCallsConsistentWithBuffer', False):
            problems.append(f'{where}: processed flag disagrees with the simulated buffer')
        proc = [c['chunk'] for c in d['chunks'] if c['processed'] and c['feed'] != 'flush']
        if proc != list(range(len(proc))):
            problems.append(f'{where}: chunk indexes not consecutive')
        for pat, o in d['otherFeedPatterns'].items():
            if not o['identicalToAligned']:
                problems.append(f'{where}: feed pattern {pat} differs from aligned')
        expected_chunks = -(-d['audioSamples'] // d['chunkSamples'])
        n_proc = len(proc) + (1 if any(c['feed'] == 'flush' for c in d['chunks']) else 0)
        if n_proc != expected_chunks:
            problems.append(f'{where}: {n_proc} chunks decoded but ceil(samples/chunk) = {expected_chunks}')
        sha = hashlib.sha256(json.dumps([c['ids'] for c in d['chunks']]).encode()).hexdigest()[:16]
        entries[cid] = {'tokens': d['tokenCount'], 'chunks': n_proc, 'samples': d['audioSamples'], 'langId': d['langId'],
                        'idsSha256_16': sha, 'bytes': os.path.getsize(path), 'text': d['text']}
    index[variant] = entries

# overlap with the earlier A2 oracle run
if a2dir and 'int4-c056' in index:
    for tag, variant in (('int4c056', 'int4-c056'), ('int4c112', 'int4-c112')):
        for stem, cid in A2_MAP.items():
            p = os.path.join(a2dir, f'oracle_{tag}_{stem}.json')
            q = os.path.join(root, variant, cid + '.json')
            if not (os.path.exists(p) and os.path.exists(q)):
                continue
            a = json.load(open(p, encoding='utf-8'))
            b = json.load(open(q, encoding='utf-8'))
            same = [c['ids'] for c in a['chunks']] == [c['ids'] for c in b['chunks']] and a['text'] == b['text']
            print(f'A2 overlap {variant}/{cid}: {"identical" if same else "DIFFERENT"}')
            if not same:
                problems.append(f'{variant}/{cid}: differs from earlier A2 oracle run')

json.dump(index, open(index_out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(f'{n_files} golden files checked, index -> {index_out}')
print('PROBLEMS:' if problems else 'no problems')
for p in problems:
    print('  ', p)
sys.exit(1 if problems else 0)
