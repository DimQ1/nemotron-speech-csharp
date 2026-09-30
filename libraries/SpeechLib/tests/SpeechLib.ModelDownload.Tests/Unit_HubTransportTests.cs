using SpeechLib.ModelDownload;
using Xunit;

namespace SpeechLib.ModelDownload.Tests;

/// <summary>
/// The Hub client is transport-agnostic: these tests drive it with a fake
/// transport so the URLs it builds are verifiable without the network. That is
/// where the "Parakeet int4 always 404" regression lived — the subfolder must be
/// stripped from the local path but kept in the URL.
/// </summary>
public sealed class Unit_HubTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vt-hub-" + Guid.NewGuid().ToString("N"));

    public Unit_HubTransportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private sealed class FakeTransport : IHubTransport
    {
        public List<string> RequestedUrls { get; } = [];

        public string Metadata { get; set; } = "{}";

        public byte[] Payload { get; set; } = [1, 2, 3, 4, 5];

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            return Task.FromResult(Metadata);
        }

        public async Task DownloadToFileAsync(
            string url, string tempPath, long expectedSize, Action<long> onBytes, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            await File.WriteAllBytesAsync(tempPath, Payload, cancellationToken).ConfigureAwait(false);
            onBytes(Payload.Length);
        }
    }

    [Fact]
    public async Task ListFiles_AsksForSizesAndKeepsTheRepoPath()
    {
        var transport = new FakeTransport
        {
            Metadata = """
            { "siblings": [ { "rfilename": "int4/config.json", "size": 97 },
                            { "rfilename": "int4/encoder-model.onnx", "size": 679397024 },
                            { "rfilename": "fp32/config.json", "size": 97 } ] }
            """
        };
        var client = new HuggingFaceClient(transport);

        var files = await client.ListFilesAsync("DimQ1/parakeet-tdt-0.6b-v3-onnx", subfolder: "int4");

        Assert.Equal(
            "https://huggingface.co/api/models/DimQ1/parakeet-tdt-0.6b-v3-onnx?blobs=true",
            Assert.Single(transport.RequestedUrls));
        Assert.Equal(2, files.Count);
        Assert.Equal("int4/config.json", files[0].RepoPath);
        Assert.Equal("config.json", files[0].LocalPath);
        Assert.Equal(679397024, files[1].SizeBytes);
    }

    [Fact]
    public async Task DownloadFile_UsesTheRepoPathInTheUrl_NotTheStrippedLocalPath()
    {
        var transport = new FakeTransport();
        var client = new HuggingFaceClient(transport);
        var file = new RemoteFile("int4/encoder-model.onnx", "encoder-model.onnx", 5);

        await client.DownloadFileAsync("DimQ1/parakeet-tdt-0.6b-v3-onnx", file, _root, _ => { }, CancellationToken.None);

        Assert.Equal(
            "https://huggingface.co/DimQ1/parakeet-tdt-0.6b-v3-onnx/resolve/main/int4/encoder-model.onnx",
            Assert.Single(transport.RequestedUrls));
        Assert.Equal(transport.Payload, await File.ReadAllBytesAsync(Path.Combine(_root, "encoder-model.onnx")));
    }

    [Fact]
    public async Task DownloadFile_CreatesNestedDirectoriesAndCleansTheTempFile()
    {
        var transport = new FakeTransport();
        var client = new HuggingFaceClient(transport);
        var file = new RemoteFile("int4/decoder/config.json", "decoder/config.json", 5);

        await client.DownloadFileAsync("repo/model", file, _root, _ => { }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "decoder", "config.json")));
        Assert.False(File.Exists(Path.Combine(_root, "decoder", "config.json.download")));
    }

    [Fact]
    public void CurlTransport_NeverActivatesWithoutWsl()
    {
        var transport = WindowsCurlHubTransport.TryCreate();

        if (transport is not null)
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")));
    }

    [Fact]
    public void CurlTransport_ArgumentsMatchTheDocumentedInvocation()
    {
        var metadata = WindowsCurlHubTransport.BuildMetadataArguments("https://huggingface.co/api/models/x");
        var download = WindowsCurlHubTransport.BuildDownloadArguments("https://huggingface.co/x/resolve/main/y");

        foreach (var arguments in new[] { metadata, download })
        {
            Assert.Contains("--fail-with-body", arguments);
            Assert.Contains("--location", arguments);
            Assert.Contains("--retry", arguments);
            Assert.Contains("3", arguments);
            Assert.Contains("--retry-all-errors", arguments);
            Assert.Contains("--connect-timeout", arguments);
            Assert.Contains("15", arguments);
            Assert.Contains("--silent", arguments);
            Assert.Contains("--show-error", arguments);
        }

        // Metadata is read straight to stdout, so curl must not write a file.
        Assert.DoesNotContain("--output", metadata);
        Assert.Equal("https://huggingface.co/api/models/x", metadata[^1]);

        // Files stream to stdout (-) because a Windows curl cannot write a Linux path.
        var downloadArguments = download.ToList();
        var outputIndex = downloadArguments.IndexOf("--output");
        Assert.True(outputIndex > 0);
        Assert.Equal("-", downloadArguments[outputIndex + 1]);
        Assert.Equal("https://huggingface.co/x/resolve/main/y", downloadArguments[^1]);
    }
}
