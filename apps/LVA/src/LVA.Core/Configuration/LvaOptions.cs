namespace LVA.Core.Configuration;

/// <summary>Root options mirroring appsettings.local.json (TZ §8).</summary>
public sealed class LvaOptions
{
    public AudioOptions Audio { get; set; } = new();
    public VadOptions Vad { get; set; } = new();
    public AsrOptions Asr { get; set; } = new();
    public IntentEngineOptions IntentEngine { get; set; } = new();
    public RouterOptions Router { get; set; } = new();
    public PluginOptions Plugins { get; set; } = new();
    public NotificationOptions Notifications { get; set; } = new();
}

public sealed class AudioOptions
{
    public int TargetSampleRate { get; set; } = 16000;
    public int FrameMs { get; set; } = 30;
}

public sealed class VadOptions
{
    public float Threshold { get; set; } = 0.5f;
    public int MinSilenceMs { get; set; } = 800;
    public int EndOfQuestionSilenceMs { get; set; } = 2500;
    public int SpeechPadMs { get; set; } = 200;
    public string ModelPath { get; set; } = "models/vad/onnx/model.onnx";
}

public sealed class AsrOptions
{
    public string Engine { get; set; } = "nemotron";
    public string ModelDir { get; set; } = "models/nemotron/cpu";
    public int LatencyMode { get; set; } = 560;
    public string ExecutionProvider { get; set; } = "cpu";
}

public sealed class IntentEngineOptions
{
    public IntentModelOptions L1 { get; set; } = new()
    {
        ModelId = "paraphrase-multilingual-MiniLM-L12-v2-int8",
        ThresholdAddressing = 0.85f
    };
    public IntentModelOptions L2 { get; set; } = new() { ModelId = "lva-distilbert-multilingual-int8" };
    public IntentModelOptions L3 { get; set; } = new() { ModelId = "bge-m3-onnx-int8", Enabled = true };
    public int HysteresisM { get; set; } = 3;
    public int HysteresisN { get; set; } = 5;
    public string IntentsFile { get; set; } = "intents.json";
    public bool RequireOwnerVoice { get; set; }
    public float HybridAlpha { get; set; } = 0.6f;
}

public sealed class IntentModelOptions
{
    public string ModelId { get; set; } = "";
    public string? ModelPath { get; set; }
    public string? TokenizerPath { get; set; }
    public float ThresholdAddressing { get; set; } = 0.85f;
    public bool Enabled { get; set; } = true;
}

public sealed class RouterOptions
{
    public string Mode { get; set; } = "rules";
}

public sealed class PluginOptions
{
    public string Directory { get; set; } = "plugins/";
    public int DefaultTimeoutSec { get; set; } = 60;
}

public sealed class NotificationOptions
{
    public bool Sound { get; set; } = true;
    public bool SystemNotifications { get; set; } = true;
    public bool Tts { get; set; }
}
