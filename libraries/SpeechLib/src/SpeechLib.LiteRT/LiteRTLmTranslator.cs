using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeechLib.LiteRT;

/// <summary>
/// Translates text through a local LiteRT-LM server exposing an OpenAI-compatible
/// <c>/v1/chat/completions</c> endpoint (the gemma-translator topology). The server
/// itself hosts a Gemma 4 model in <c>.litertlm</c> format and runs fully offline.
/// </summary>
public sealed class LiteRTLmTranslator : ITextTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LiteRTLmOptions _options;
    private readonly bool _ownsClient;

    public LiteRTLmTranslator(LiteRTLmOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new LiteRTLmOptions();
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();

        if (_ownsClient)
        {
            // Streaming responses are a single long-lived connection. The default
            // 100 s HttpClient.Timeout would cut off slow token streams; the caller
            // governs lifetime via the per-call CancellationToken instead.
            _http.Timeout = Timeout.InfiniteTimeSpan;
        }

        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(_options.BaseUrl);
    }

    /// <inheritdoc />
    public async Task<string?> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        using var response = await _http
            .PostAsync(
                _options.Endpoint,
                CreateJsonContent(BuildRequest(text, targetLang, sourceLang, stream: false)),
                cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var content = payload?.Choices?.FirstOrDefault()?.Message?.Content;
        return string.IsNullOrWhiteSpace(content) ? null : ExtractTranslation(content);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> TranslateStreamAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = CreateJsonContent(BuildRequest(text, targetLang, sourceLang, stream: true)),
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null &&
            mediaType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
        {
            // Server ignored "stream" and returned a single JSON payload — fall back
            // to emitting the whole translation as one delta.
            var payload = await response.Content
                .ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var content = payload?.Choices?.FirstOrDefault()?.Message?.Content;
            if (!string.IsNullOrWhiteSpace(content))
                yield return ExtractTranslation(content) ?? string.Empty;

            yield break;
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
                yield break;
            if (data.Length == 0)
                continue;

            var delta = ExtractStreamDelta(data);
            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
    }

    /// <summary>
    /// Serializes the request up-front so the body carries a <c>Content-Length</c>
    /// header. <see cref="JsonContent.Create{T}(T, JsonSerializerOptions?)"/> does not
    /// compute a length, which makes <see cref="HttpClient"/> fall back to chunked
    /// transfer-encoding; some minimal OpenAI-compatible servers (e.g. bare
    /// <c>http.server</c> shims) cannot decode chunked bodies.
    /// </summary>
    private static StringContent CreateJsonContent<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private ChatCompletionRequest BuildRequest(
        string text,
        string targetLang,
        string? sourceLang,
        bool stream) =>
        new()
        {
            Model = _options.Model,
            Messages =
            [
                new ChatMessage("system", _options.BuildSystemPrompt(targetLang, sourceLang)),
                new ChatMessage("user", text),
            ],
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
            Stream = stream,
        };

    private static string? ExtractStreamDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore malformed SSE lines.
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    /// <summary>
    /// The prompt asks for plain text, but some models (or an upstream server with
    /// its own gemma-translator prompt) answer with a <c>{"translation":"..."}</c>
    /// envelope — tolerate both.
    /// </summary>
    private static string? ExtractTranslation(string content)
    {
        content = content.Trim();
        if (!content.StartsWith('{'))
            return content;

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("translation", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            // Not JSON after all — fall through to raw text.
        }

        return content;
    }

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";

        [JsonPropertyName("messages")] public ChatMessage[] Messages { get; init; } = [];

        [JsonPropertyName("temperature")] public float Temperature { get; init; }

        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }

        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public ChatChoice[]? Choices { get; init; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")] public ChatResponseMessage? Message { get; init; }
    }

    private sealed class ChatResponseMessage
    {
        [JsonPropertyName("content")] public string? Content { get; init; }
    }
}
