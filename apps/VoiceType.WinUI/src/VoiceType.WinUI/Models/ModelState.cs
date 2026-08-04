namespace VoiceType.WinUI.Models;

/// <summary>
/// Lifecycle states for the speech recognition model (ONNX).
/// Separate from capture states (Idle/Listening/Muted) —
/// the model can be Loaded while capture is Idle.
/// </summary>
public enum ModelState
{
    /// <summary>Model is not loaded into memory.</summary>
    Unloaded,

    /// <summary>Model is being loaded from disk (potentially slow).</summary>
    Loading,

    /// <summary>Model is loaded and ready for recognition.</summary>
    Loaded,

    /// <summary>Model loading failed (invalid path, corrupt files, etc.).</summary>
    Error
}
