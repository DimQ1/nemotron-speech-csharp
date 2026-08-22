using System.Net;
using System.Net.Sockets;
using System.Text;
using SpeechLib.LiteRT;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// End-to-end tests that exercise <see cref="LiteRTLmTranslator"/> against a real
/// TCP socket, verifying the full HTTP + SSE wire path without any network
/// dependency beyond the loopback interface.
/// </summary>
public sealed class E2E_LiteRTLmTranslatorTests
{
    [Fact]
    public async Task TranslateStreamAsync_AgainstLocalServer_StreamsTokens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(() => ServeOnce(listener));

        using var translator = new LiteRTLmTranslator(new LiteRTLmOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var tokens = new List<string>();
        await foreach (var token in translator.TranslateStreamAsync("Hello", "de", cancellationToken: cts.Token))
            tokens.Add(token);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(new[] { "Hal", "lo" }, tokens);
    }

    private static async Task ServeOnce(TcpListener listener)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            // Consume the request line and headers (plus any body the reader
            // happens to buffer) before writing the response.
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            string? line;
            do
            {
                line = await reader.ReadLineAsync();
            }
            while (!string.IsNullOrEmpty(line));

            var response =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Connection: close\r\n" +
                "\r\n" +
                "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hal\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";

            await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            await stream.FlushAsync();
        }
        finally
        {
            listener.Stop();
        }
    }
}
