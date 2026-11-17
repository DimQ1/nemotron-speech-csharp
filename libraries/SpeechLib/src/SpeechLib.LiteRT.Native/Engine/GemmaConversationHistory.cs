using SpeechLib.Llm;

namespace SpeechLib.LiteRT.Native;

internal sealed record GemmaConversationSnapshot(
    GemmaConversationSpec Spec,
    IReadOnlyList<LlmMessage> Turns) : IGemmaSnapshot;

/// <summary>Text-only replay state owned by the application, not by a native engine or its KV cache.</summary>
internal sealed class GemmaConversationHistory
{
    private readonly GemmaConversationSpec _spec;
    private readonly List<LlmMessage> _turns;
    private bool _pending;

    public GemmaConversationHistory(GemmaConversationSpec spec)
    {
        _turns = [.. spec.History];
        _spec = spec with { History = _turns.ToArray() };
    }

    public void BeginTurn(string text)
    {
        if (_pending)
            throw new InvalidOperationException("The interrupted conversation cannot accept another turn.");
        _turns.Add(new LlmMessage(LlmRole.User, text));
        _pending = true;
    }

    public void CompleteTurn(string answer)
    {
        if (!_pending)
            throw new InvalidOperationException("There is no pending user turn.");
        _turns.Add(new LlmMessage(LlmRole.Assistant, answer));
        _pending = false;
    }

    public GemmaConversationSnapshot Snapshot(string? partialResponse)
    {
        var turns = new List<LlmMessage>(_turns);
        if (!string.IsNullOrEmpty(partialResponse))
        {
            if (!_pending)
                throw new InvalidOperationException("Partial text requires an interrupted user turn.");
            turns.Add(new LlmMessage(LlmRole.Assistant, partialResponse));
        }
        return new GemmaConversationSnapshot(_spec, turns.AsReadOnly());
    }
}
