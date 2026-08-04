namespace LocalVoiceAssistant.Maui.Models;

public sealed record AssistantAction(string Id, string Title, string Details);

public sealed record AssistantResponse(string Message, bool ShouldStop = false, bool IsWarning = false);
