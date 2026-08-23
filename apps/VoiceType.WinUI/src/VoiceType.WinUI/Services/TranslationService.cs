using System.Text;
using SpeechLib.LiteRT.Native;
using SpeechLib.Translation;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Buffers the streaming transcript, splits off complete sentences, and streams each
/// sentence's translation through the in-process LiteRT-LM translator (Gemma 4 E2B).
/// Mirrors the CLI <c>LiveTranslationCoordinator</c>, but surfaces events for the UI.
/// </summary>
/// <remarks>
/// <see cref="Feed"/>, <see cref="FlushAsync"/>, and <see cref="Reset"/> are expected to be
/// called from the UI thread. Translation work runs on the thread pool; the
/// <c>TranslationChanged</c>/<c>StatusChanged</c> events are raised from worker threads.
/// </remarks>
public sealed class TranslationService : ITranslationService
{
    private readonly object _bufferLock = new();
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _completed = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _inflightLock = new();
    private readonly List<Task> _inflight = new();

    private LiteRTLmNativeTranslator? _translator;
    private volatile string _targetLanguage = "ru";
    private volatile string _statusText = "Translation off";
    private volatile bool _isLoaded;
    private volatile bool _isLoading;
    private string _streaming = "";
    private int _consumed;
    private int _fedLength;

    public event Action<string>? TranslationChanged;
    public event Action<string>? StatusChanged;

    public bool IsModelAvailable => TranslationModelInfo.IsDownloaded;
    public bool IsLoaded => _isLoaded;
    public bool IsLoading => _isLoading;
    public string StatusText => _statusText;

    public void SetTargetLanguage(string language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            _targetLanguage = language;
    }

    public void Feed(string fullText)
    {
        if (string.IsNullOrEmpty(fullText))
            return;

        string delta;
        lock (_bufferLock)
        {
            var start = Math.Clamp(_fedLength, 0, fullText.Length);
            delta = fullText[start..];
            _fedLength = fullText.Length;

            if (delta.Length == 0)
                return;

            _buffer.Append(delta);
            var sentences = SentenceSplitter.ExtractCompleteSentences(_buffer, ref _consumed);
            foreach (var sentence in sentences)
                Enqueue(sentence);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        string tail;
        lock (_bufferLock)
        {
            tail = _buffer.ToString(_consumed, _buffer.Length - _consumed).Trim();
            _buffer.Clear();
            _consumed = 0;
            _fedLength = 0;
        }

        if (tail.Length > 0)
            Enqueue(tail);

        Task[] pending;
        lock (_inflightLock)
        {
            pending = _inflight.Where(t => !t.IsCompleted).ToArray();
            _inflight.Clear();
        }

        if (pending.Length == 0)
            return;

        try
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort flush: cancellation leaves the remaining translations unfinished.
        }
    }

    public void Reset()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
            _consumed = 0;
            _fedLength = 0;
        }

        _completed.Clear();
        _streaming = "";
        RaiseTranslationChanged();
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded || !IsModelAvailable)
            return;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isLoaded || _translator is not null)
                return;

            if (!IsModelAvailable)
            {
                SetStatus("Translation model not downloaded");
                return;
            }

            _isLoading = true;
            SetStatus("Loading translation model...");

            try
            {
                _translator = await Task.Run(() =>
                {
                    var options = new LiteRTLmNativeOptions
                    {
                        ModelPath = TranslationModelInfo.LocalModelPath,
                        Backend = TranslationModelInfo.Backend,
                        LogLevel = LiteRTLmLogLevel.Silent,
                    };
                    return new LiteRTLmNativeTranslator(options);
                }, cancellationToken).ConfigureAwait(false);

                _isLoaded = true;
                SetStatus("Translation model ready");
            }
            catch (Exception ex)
            {
                _translator?.Dispose();
                _translator = null;
                _isLoaded = false;
                SetStatus($"Translation model error: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _translator?.Dispose();
        _translator = null;
        _writeGate.Dispose();
        _loadGate.Dispose();

        // Give in-flight tasks a moment to observe the disposed state; they hold
        // their own references and will fail gracefully on the disposed engine.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void Enqueue(string sentence)
    {
        var task = TranslateSentenceAsync(sentence, _targetLanguage);
        lock (_inflightLock)
        {
            _inflight.RemoveAll(t => t.IsCompleted);
            _inflight.Add(task);
        }
    }

    private async Task TranslateSentenceAsync(string sentence, string language)
    {
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetStatus($"Translation model failed to load: {ex.Message}");
            return;
        }

        var translator = _translator;
        if (translator is null)
            return;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var partial = new StringBuilder();
            await foreach (var token in translator.TranslateStreamAsync(sentence, language).ConfigureAwait(false))
            {
                partial.Append(token);
                _streaming = partial.ToString();
                RaiseTranslationChanged();
            }

            var result = partial.ToString().Trim();
            if (result.Length > 0)
            {
                if (_completed.Length > 0)
                    _completed.AppendLine();
                _completed.Append(result);
            }

            _streaming = "";
            RaiseTranslationChanged();
        }
        catch (Exception ex)
        {
            _streaming = "";
            SetStatus($"Translation error: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void RaiseTranslationChanged()
    {
        var sb = new StringBuilder();
        if (_completed.Length > 0)
        {
            sb.Append(_completed);
            if (_streaming.Length > 0)
                sb.AppendLine();
        }
        sb.Append(_streaming);

        TranslationChanged?.Invoke(sb.ToString());
    }

    private void SetStatus(string status)
    {
        _statusText = status;
        StatusChanged?.Invoke(status);
    }
}
