"""Token-id comparison of golden runs made with different intra-op thread counts.
usage: python compare_threads.py <goldenNemotronDir> <out.json>   (compares <dir>/int4-c056 [threads 4] with <dir>/checks/threads{1,12}/int4-c056)"""
import glob, json, os, sys
root, out = sys.argv[1:3]
report = {}
for t in (1, 12):
    d = os.path.join(root, 'checks', f'threads{t}', 'int4-c056')
    rows = []
    for p in sorted(glob.glob(os.path.join(d, '*.json'))):
        a = json.load(open(os.path.join(root, 'int4-c056', os.path.basename(p)), encoding='utf-8'))
        b = json.load(open(p, encoding='utf-8'))
        same = [c['ids'] for c in a['chunks']] == [c['ids'] for c in b['chunks']] and a['text'] == b['text']
        rows.append({'clip': a['clip'], 'identicalToThreads4': same, 'tokens': a['tokenCount']})
    report[f'threads{t}'] = {'clips': len(rows), 'identical': sum(r['identicalToThreads4'] for r in rows), 'different': [r['clip'] for r in rows if not r['identicalToThreads4']]}
    print(t, report[f'threads{t}'])
json.dump(report, open(out, 'w'), indent=1)
