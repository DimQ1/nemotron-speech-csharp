using System.Net;
using System.Text;
using SpeechLib.LiteRT;
using Xunit;

namespace VoiceType.Tests;

public sealed class Unit_LiteRTLmTranslatorTests
{
    [Fact]
    public async Task TranslateAsync_WhenJsonEnvelope_ParsesTranslation()
    {
        var handler = new StubHandler("""{"choices":[{"message":{"content":"{\"translation\":\"Hallo Welt\"}"}}]}""");
        using var client = new HttpClient(handler);
        using var translator = new LiteRTLmTranslator(
            new LiteRTLmOptions { BaseUrl = "http://localhost:9379" }, client);

        var result = await translator.TranslateAsync("Hello world", "de");

        Assert.Equal("Hallo Welt", result);
    }

    [Fact]
    public async Task TranslateAsync_WhenPlainText_ReturnsTextUnchanged()
    {
        var handler = new StubHandler("""{"choices":[{"message":{"content":"Hallo Welt"}}]}""");
        using var client = new HttpClient(handler);
        using var translator = new LiteRTLmTranslator(new LiteRTLmOptions(), client);

        var result = await translator.TranslateAsync("Hello world", "de");

        Assert.Equal("Hallo Welt", result);
    }

    [Fact]
    public async Task TranslateStreamAsync_WhenSse_YieldsTokenDeltas()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hal\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHandler(sse, "text/event-stream");
        using var client = new HttpClient(handler);
        using var translator = new LiteRTLmTranslator(new LiteRTLmOptions(), client);

        var tokens = new List<string>();
        await foreach (var token in translator.TranslateStreamAsync("Hello world", "de"))
            tokens.Add(token);

        Assert.Equal(new[] { "Hal", "lo" }, tokens);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly string _mediaType;

        public StubHandler(string content, string mediaType = "application/json")
        {
            _content = content;
            _mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_content, Encoding.UTF8, _mediaType),
            };
            return Task.FromResult(response);
        }
    }
}
