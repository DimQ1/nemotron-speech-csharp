# VoiceType Privacy Policy

**Last updated:** 2026-07-26

## Summary

**VoiceType does NOT collect, transmit, or sell your personal data.** All processing happens locally on your device. VoiceType is an offline-first application — after initial model download, no internet connection is required.

---

## Data Collection & Processing

### Audio Recordings

- Audio is captured from your microphone for the sole purpose of speech-to-text transcription.
- All audio processing is performed **locally on your device** using the Nemotron ASR engine (ONNX Runtime).
- **Audio is never uploaded to any server or cloud service.**
- Audio is discarded immediately after transcription.

### Recognized Text

- Transcribed text is injected directly into the application you are actively using (via Windows text input APIs).
- Text is **not stored, logged, or transmitted** by VoiceType.
- You control where the text goes — it is typed into whichever window you have focused.

### Model Downloads

- On first launch, VoiceType downloads speech recognition model files from HuggingFace (huggingface.co).
- These are anonymous HTTP requests containing **no personal data** — only the model file identifiers.
- After download, all model inference runs **entirely offline** on your device.

### Telemetry & Diagnostics (Development Mode Only)

- In **development builds only**, anonymous performance logs may be exported to a local OpenTelemetry endpoint (Aspire Dashboard running on your machine).
- This telemetry is **never enabled in production Store builds**.
- No telemetry data leaves your local network.

---

## Third-Party Services

### HuggingFace (huggingface.co)

- Used exclusively for downloading ASR model files.
- No personal data is shared with HuggingFace.
- See [HuggingFace Privacy Policy](https://huggingface.co/privacy) for their data practices.

### ONNX Runtime

- The ONNX Runtime inference engine runs entirely on-device.
- No data is sent to Microsoft or any third party during inference.

---

## Data Storage

- The default storage is per-user and differs by deployment:
  - Installed MSIX/Store build: `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\VoiceType\`
  - Unpackaged development build: `%LOCALAPPDATA%\VoiceType\`
- Settings are stored in `settings.json`, models in `Models\`, and sessions in `Sessions\` under that root.
- The model and session folders can be changed in Settings; configured paths are preserved and used by the corresponding services.
- Store/MSIX installs do not provide shared all-users storage. A shared model directory requires separate provisioning or an all-users installer with appropriate permissions.
- No data is stored outside your device unless you explicitly choose another folder or download a model from HuggingFace.

---

## Your Rights (GDPR / CCPA)

Since VoiceType does not collect any personal data:

- There is **no personal data to access, delete, correct, or port**.
- The app is fully functional offline — you remain in complete control of your data.

---

## Children's Privacy

VoiceType does not knowingly collect data from children under 13. The app is rated **3+ (E for Everyone)** as it contains no user-generated content, social features, or data collection.

---

## Changes to This Policy

If our data practices change, we will update this policy and notify users through the Microsoft Store listing. Continued use of the app after policy changes constitutes acceptance.

---

## Contact

For privacy questions or data requests:

- **Email:** [your-email@example.com]
- **GitHub:** [github.com/DimQ1/nemotron-speech-csharp](https://github.com/DimQ1/nemotron-speech-csharp)

---

**VoiceType — Your voice stays on your device. Always.**
