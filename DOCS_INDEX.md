# Docs Index

Quick navigation for the most important project documents.

## Start here

- Project overview: [README.md](README.md)
- Agent/dev workflow notes: [AGENTS.md](AGENTS.md)
- Documentation map: [docs/README.md](docs/README.md)
- CI/CD (workflows, tests in CI, releases, Android signing, open decisions): [docs/ci.md](docs/ci.md)

## Store publishing

- Store submission guide: [VoiceType.WinUI/StoreSubmissionGuide.md](apps/VoiceType.WinUI/src/VoiceType.WinUI/StoreSubmissionGuide.md)
- Store CSV imports (all): [docs/store/imports](docs/store/imports)
- EN/RU full import template: [docs/store/imports/listingData-store-import-v1.1.29-en-ru-full-template.csv](docs/store/imports/listingData-store-import-v1.1.29-en-ru-full-template.csv)
- EN/RU compact import template: [docs/store/imports/listingData-store-import-v1.1.29-en-ru.csv](docs/store/imports/listingData-store-import-v1.1.29-en-ru.csv)
- Multilang import template: [docs/store/imports/listingData-store-import-v1.1.29-multilang.csv](docs/store/imports/listingData-store-import-v1.1.29-multilang.csv)
- Certification artifacts: [docs/store/certification](docs/store/certification)

## WinUI app

- App-level technical README: [VoiceType.WinUI/README.md](apps/VoiceType.WinUI/src/VoiceType.WinUI/README.md)
- Local dev MSIX build/install scripts:
  - [VoiceType.WinUI/build-dev.ps1](apps/VoiceType.WinUI/src/VoiceType.WinUI/build-dev.ps1)
  - [VoiceType.WinUI/install-dev.ps1](apps/VoiceType.WinUI/src/VoiceType.WinUI/install-dev.ps1)
- Store release packaging script: [VoiceType.WinUI/build-store-release.ps1](apps/VoiceType.WinUI/src/VoiceType.WinUI/build-store-release.ps1)

## Research and plans

- ASR timestamp analysis: [docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md](docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md)
- ONNX Runtime GenAI idea backlog (recipes for the plain-ORT recognizers): [docs/research/onnxruntime-genai-ideas.md](docs/research/onnxruntime-genai-ideas.md)
- Nemotron on plain ONNX Runtime, limitations and next candidates: [docs/research/nemotron-pure-ort-followups.md](docs/research/nemotron-pure-ort-followups.md)
- Nemotron on WebGPU or CPU, provider policy and measurements: [docs/research/nemotron-webgpu.md](docs/research/nemotron-webgpu.md)
- Nemotron golden oracle (recorded from GenAI 0.16.0): harness and regeneration in [tools/nemotron-golden/README.md](tools/nemotron-golden/README.md), data in `libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/Data/golden-full`
- ONNX opset analysis: [docs/research/onnx/opset24-analysis.md](docs/research/onnx/opset24-analysis.md)
- ONNX conversion notes: [docs/research/onnx/opset24-conversion.md](docs/research/onnx/opset24-conversion.md)
