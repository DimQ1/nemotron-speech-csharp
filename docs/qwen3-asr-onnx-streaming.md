# Qwen3-ASR ONNX Streaming

The regular `qwen3-asr-1.7b-onnx` folder uses its default four-second update
cadence. For the separate ONNX streaming profile, use the
`models/qwen3-asr-1.7b-onnx-streaming` manifest:

```json
{
  "model_type": "qwen3_asr_onnx_streaming",
  "base_model_dir": "../qwen3-asr-1.7b-onnx",
  "chunk_seconds": 2.0
}
```

The provider accepts normal 100 ms input callbacks and buffers them until the
configured `chunk_seconds` boundary. At each boundary it re-runs the ONNX
encoder and KV-cache decoder over all audio received so far, then emits only the
stable text delta. A final short tail is decoded by `Flush`; `ResetStreamingState`
starts a new stream.

This matches the official Qwen streaming algorithm's context behavior, while
using the ONNX Runtime export instead of vLLM. It is not a persistent
audio-encoder cache: the published Qwen3-ASR encoder has no audio state
input/output, so strict O(1) incremental encoder computation would require a
causal/stateful encoder export or a separately trained streaming model. The
current profile preserves full-context accuracy rather than decoding each audio
chunk independently, which would lose words and language context at boundaries.
