#!/usr/bin/env python3
"""Checks the VoiceType.Uno interface string files without a build (the same rules as the Localization tests).

usage: python tools/scripts/check-ui-strings.py [language ...]   (default: every language folder)

For each language: the same files as en, every English key (plural keys with the language's CLDR categories),
no stray keys, the same {n} placeholders (a one/zero/two form may drop them), balanced braces, the same
**bold** / *italic* / \\n markers count as English for Help texts.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "apps/VoiceType.Uno/src/VoiceType.Uno.Core/Localization/Strings"
CATS = {"zh-CN": ["other"], "ja": ["other"], "ko": ["other"],
        "ru": ["one", "few", "many", "other"], "uk": ["one", "few", "many", "other"], "pl": ["one", "few", "many", "other"],
        "ar": ["zero", "one", "two", "few", "many", "other"]}
PLURAL = {"zero", "one", "two", "few", "many", "other"}
PH = re.compile(r"\{(\d+)(?:[,:][^}]*)?\}")


def load(lang):
    files = {}
    for f in sorted((ROOT / lang).glob("*.json")):
        files[f.name] = json.loads(f.read_text(encoding="utf-8"))
    return files


def placeholders(s):
    return {int(m.group(1)) for m in PH.finditer(s.replace("{{", "").replace("}}", ""))}


def braces_ok(s):
    t = PH.sub("", s.replace("{{", "").replace("}}", ""))
    return "{" not in t and "}" not in t


def main():
    en_files = load("en")
    en = {k: v for d in en_files.values() for k, v in d.items()}
    langs = sys.argv[1:] or sorted(p.name for p in ROOT.iterdir() if p.is_dir() and p.name != "en")
    bad_total = 0
    for lang in langs:
        problems = []
        files = load(lang)
        if set(files) != set(en_files):
            problems.append(f"files differ: missing {sorted(set(en_files) - set(files))}, extra {sorted(set(files) - set(en_files))}")
        cats = CATS.get(lang, ["one", "other"])
        for name, en_d in en_files.items():
            d = files.get(name, {})
            bases = {k.rsplit(".", 1)[0] for k in en_d if k.rsplit(".", 1)[-1] in PLURAL}
            expected = {k for k in en_d if not (k.rsplit(".", 1)[-1] in PLURAL and k.rsplit(".", 1)[0] in bases)}
            expected |= {f"{b}.{c}" for b in bases for c in cats}
            for k in sorted(expected - set(d)):
                problems.append(f"{name}: missing {k}")
            for k in sorted(set(d) - expected):
                if not (k.rsplit(".", 1)[-1] in PLURAL and k.rsplit(".", 1)[0] in bases):
                    problems.append(f"{name}: stray {k}")
            for k, v in d.items():
                if not isinstance(v, str) or not v.strip():
                    problems.append(f"{name}: empty {k}")
                    continue
                base, _, tail = k.rpartition(".")
                src = en_d.get(k) or (en_d.get(base + ".other") if tail in PLURAL else None)
                if src is None:
                    continue
                exp, act = placeholders(src), placeholders(v)
                if (act <= exp) if tail in ("one", "zero", "two") else (act == exp):
                    pass
                else:
                    problems.append(f"{name}: placeholders {k}: en {sorted(exp)} vs {sorted(act)}")
                if not braces_ok(v):
                    problems.append(f"{name}: braces {k}")
                for marker in ("**", "\n"):
                    if src.count(marker) != v.count(marker):
                        problems.append(f"{name}: marker {marker!r} count {k}")
        bad_total += len(problems)
        print(f"{lang}: {'OK' if not problems else str(len(problems)) + ' problems'}")
        for p in problems[:60]:
            print("   ", p)
    sys.exit(1 if bad_total else 0)


if __name__ == "__main__":
    main()
