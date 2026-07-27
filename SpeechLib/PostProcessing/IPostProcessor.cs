namespace SpeechLib.PostProcessing;

/// <summary>
/// Chain of Responsibility handler for post-processing text.
/// Each handler can transform text, skip remaining handlers, or pass to the next.
/// </summary>
public interface IPostProcessor
{
    /// <summary>
    /// Process the input text. Return null to skip remaining handlers (early exit).
    /// </summary>
    string? Process(string text);

    /// <summary>Next handler in the chain.</summary>
    IPostProcessor? Next { get; set; }
}

/// <summary>
/// Base class for post-processing handlers. Implements the chain linkage.
/// </summary>
public abstract class PostProcessorBase : IPostProcessor
{
    public IPostProcessor? Next { get; set; }

    public string? Process(string text)
    {
        var result = ProcessCore(text);
        if (result is null || Next is null)
            return result;
        return Next.Process(result);
    }

    /// <summary>
    /// Override to implement the actual processing logic.
    /// Return null to stop the chain early.
    /// </summary>
    protected abstract string? ProcessCore(string text);
}

/// <summary>
/// Builds and executes a chain of post-processing handlers.
/// </summary>
public sealed class PostProcessingChain
{
    private IPostProcessor? _head;

    /// <summary>Add a handler to the end of the chain.</summary>
    public PostProcessingChain Add(IPostProcessor handler)
    {
        if (_head is null)
        {
            _head = handler;
        }
        else
        {
            var current = _head;
            while (current.Next is not null)
                current = current.Next;
            current.Next = handler;
        }
        return this;
    }

    /// <summary>Execute the chain. Returns the final processed text.</summary>
    public string Execute(string text)
    {
        if (_head is null) return text;
        return _head.Process(text) ?? string.Empty;
    }
}
