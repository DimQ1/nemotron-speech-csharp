# Sortformer Streaming

Pure C# implementation of Sortformer streaming speaker-cache updates for
diarization inference. The source is intentionally project-independent and
contains no executable host or model files.

- `Models/` contains the streaming state and configuration.
- `Services/` contains the state update algorithm and logging abstraction.