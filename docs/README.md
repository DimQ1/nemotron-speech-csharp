# Documentation

This is the single documentation root. Public static-hosting files remain at the
root so existing Store and website links keep working.

## Public materials

- `privacy.html` - public privacy policy page for Store listing and website links.
- `store-screenshots/` - visual assets used for Microsoft Store listing screenshots.

## Internal technical materials

- `ci.md` - the GitHub Actions pipelines: what runs when, artifacts, secrets (names), releases, running the same checks locally, what CI does not cover, the iOS staged path, open decisions.
- `design/` - design artefacts and UI mockups.
- `plans/` - product and technical implementation plans.
- `research/asr/` - ASR behavior analysis and timestamp investigations.
- `research/onnxruntime-genai-ideas.md` - idea backlog mined from the upstream ONNX Runtime GenAI source (the product no longer uses GenAI).
- `research/nemotron-pure-ort-followups.md` - known limitations of the plain-ONNX-Runtime Nemotron recognizer and the next-round candidates.
- `research/nemotron-webgpu.md` - Nemotron on WebGPU or CPU: the provider policy (encoder only, discrete GPU only), measurements, crash/fallback behaviour, how the plugin ships, what is not verified.
- `research/onnx/` - ONNX/opset conversion and compatibility analysis.
- `store/imports/` - Microsoft Store listing CSV exports and import templates.
- `store/certification/` - Store/WACK certification evidence and support files.

## Quick links

- CI/CD (workflows, tests in CI, release steps, Android signing, open decisions): `ci.md`
- Store import (EN/RU full): `store/imports/listingData-store-import-v1.1.29-en-ru-full-template.csv`
- ASR timestamps analysis (with the implementation status of the plain-ONNX-Runtime route): `research/asr/nemotron-3.5-asr-timestamps-analysis.md`
- ONNX Runtime GenAI idea backlog (standing source of recipes for the plain-ORT recognizers): `research/onnxruntime-genai-ideas.md`
- Nemotron on plain ONNX Runtime: limitations and next candidates: `research/nemotron-pure-ort-followups.md`; the GenAI parity oracle and its harness: `../tools/nemotron-golden/README.md`
- ONNX opset conversion notes: `research/onnx/opset24-conversion.md`
- WinUI migration plan: `plans/voicetype-winui3-migration-plan.md`
- VoiceType.Uno mobile redesign (design, mockup, overlap check): `design/voicetype-uno-mobile/README.md`
- VoiceType.Uno recordings history (design only: playback, Gemma chat, translation, versioned re-recognition): `design/voicetype-uno-history/README.md`
- Gemma engine (LiteRT-LM / LiteRtLmSharp): measurements, how to improve it and what was shipped in the
  fork (second engine, Snapshot/Resume, non-blocking TokenCount, sampler diagnostics): `research/litert/engine-improvements.md`
