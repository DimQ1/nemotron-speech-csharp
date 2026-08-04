using CommunityToolkit.Mvvm.Messaging.Messages;

namespace VoiceType.WinUI.Messages;

/// <summary>Broadcast when a model has been successfully downloaded.</summary>
public sealed class ModelDownloadedMessage : ValueChangedMessage<(string ModelsRootPath, string ModelPath)>
{
    public ModelDownloadedMessage(string modelsRootPath, string modelPath)
        : base((modelsRootPath, modelPath)) { }
}

/// <summary>Broadcast when settings have been saved from the Settings window.</summary>
public sealed class SettingsSavedMessage : ValueChangedMessage<Models.AppSettings>
{
    public SettingsSavedMessage(Models.AppSettings settings) : base(settings) { }
}