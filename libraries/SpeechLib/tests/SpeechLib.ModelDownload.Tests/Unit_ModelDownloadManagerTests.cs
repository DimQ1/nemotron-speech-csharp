using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SpeechLib.ModelDownload;
using Xunit;

namespace SpeechLib.ModelDownload.Tests;

public sealed class Unit_ModelDownloadManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vt-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>In-memory Hugging Face: repo listing plus resolve URLs with Range support.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _repos = new();
        public TimeSpan ChunkDelay { get; set; }
        public int ChunkSize { get; set; } = 1024;
        public int ConcurrentTransfers;
        public int MaxConcurrentTransfers;
        public List<string> RangeRequests { get; } = new();
        public string? FailFile { get; set; }
        public int FileRequests;

        public void Add(string repo, string path, int size)
        {
            if (!_repos.TryGetValue(repo, out var files))
                _repos[repo] = files = new();
            var data = new byte[size];
            new Random(size).NextBytes(data);
            files[path] = data;
        }

        public byte[] Content(string repo, string path) => _repos[repo][path];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsolutePath;
            if (url.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                var repo = url["/api/models/".Length..];
                var siblings = string.Join(",", _repos[repo].Select(f => $"{{\"rfilename\":\"{f.Key}\",\"size\":{f.Value.Length}}}"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"siblings\":[{{\"rfilename\":\".gitattributes\",\"size\":1}},{siblings}]}}", Encoding.UTF8, "application/json"),
                };
            }

            var marker = "/resolve/main/";
            var at = url.IndexOf(marker, StringComparison.Ordinal);
            var repoId = url[1..at];
            var path = Uri.UnescapeDataString(url[(at + marker.Length)..]);
            Interlocked.Increment(ref FileRequests);
            if (path == FailFile)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            var data = _repos[repoId][path];
            var from = 0L;
            var status = HttpStatusCode.OK;
            if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } start })
            {
                lock (RangeRequests)
                    RangeRequests.Add($"{path}@{start}");
                from = start;
                status = HttpStatusCode.PartialContent;
            }

            var stream = new SlowStream(data.AsMemory((int)from).ToArray(), this, cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = data.Length - from;
            return await Task.FromResult(response);
        }

        private sealed class SlowStream : MemoryStream
        {
            private readonly FakeHub _hub;
            private readonly CancellationToken _ct;
            private bool _counted;

            public SlowStream(byte[] data, FakeHub hub, CancellationToken ct) : base(data)
            {
                _hub = hub;
                _ct = ct;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_counted)
                {
                    _counted = true;
                    var now = Interlocked.Increment(ref _hub.ConcurrentTransfers);
                    lock (_hub)
                        _hub.MaxConcurrentTransfers = Math.Max(_hub.MaxConcurrentTransfers, now);
                }

                if (_hub.ChunkDelay > TimeSpan.Zero)
                    await Task.Delay(_hub.ChunkDelay, cancellationToken);
                var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, _hub.ChunkSize)], cancellationToken);
                if (read == 0)
                    Interlocked.Decrement(ref _hub.ConcurrentTransfers);
                return read;
            }
        }
    }

    private (ModelDownloadManager Manager, FakeHub Hub) Create(int concurrency = 3)
    {
        var hub = new FakeHub();
        var client = new HuggingFaceClient(new HttpClient(hub), "https://hub.test");
        return (new ModelDownloadManager(client, concurrency, TimeSpan.Zero), hub);
    }

    private ModelDownloadRequest Request(string repo, string? subfolder = null) =>
        new(repo + "/" + subfolder, repo, repo, Path.Combine(_root, repo.Replace('/', '_') + subfolder)) { Subfolder = subfolder };

    [Fact]
    public async Task Download_WritesFilesAndStripsSubfolder()
    {
        var (manager, hub) = Create();
        hub.Add("u/parakeet", "int4/encoder-model.onnx", 5000);
        hub.Add("u/parakeet", "int4/vocab.txt", 300);
        hub.Add("u/parakeet", "fp32/encoder-model.onnx", 9000);

        var job = manager.Enqueue(Request("u/parakeet", "int4"));
        var path = await job.Completion;

        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(hub.Content("u/parakeet", "int4/encoder-model.onnx"), File.ReadAllBytes(Path.Combine(path, "encoder-model.onnx")));
        Assert.True(File.Exists(Path.Combine(path, "vocab.txt")));
        Assert.False(File.Exists(Path.Combine(path, "fp32", "encoder-model.onnx")));
        Assert.Equal(2, job.FilesTotal);
        Assert.Equal(5300, job.TotalBytes);
        Assert.Equal(100, job.Percent);
    }

    [Fact]
    public async Task SeveralModels_DownloadInParallelWithPerJobAndTotalProgress()
    {
        var (manager, hub) = Create(concurrency: 3);
        hub.ChunkDelay = TimeSpan.FromMilliseconds(5);
        foreach (var repo in new[] { "u/a", "u/b", "u/c" })
            hub.Add(repo, "model.onnx", 40_000);

        var updates = 0;
        manager.JobUpdated += _ => Interlocked.Increment(ref updates);
        var finished = new List<string>();
        manager.JobFinished += j => { lock (finished) finished.Add(j.Title); };

        var jobs = new[] { "u/a", "u/b", "u/c" }.Select(r => manager.Enqueue(Request(r))).ToList();
        var midway = false;
        while (jobs.Any(j => j.IsActive))
        {
            var totals = manager.Totals;
            if (totals.Percent is > 0 and < 100 && jobs.Count(j => j.Percent is > 0 and < 100) >= 2)
                midway = true;
            await Task.Delay(10);
        }
        await Task.WhenAll(jobs.Select(j => j.Completion));

        Assert.True(hub.MaxConcurrentTransfers >= 2, $"max concurrent transfers: {hub.MaxConcurrentTransfers}");
        Assert.True(midway, "two jobs should have been in progress at the same time");
        Assert.True(updates > 6);
        Assert.Equal(3, finished.Count);
        Assert.Equal(100, manager.Totals.Percent);
        Assert.Equal(3, manager.Totals.Completed);
    }

    [Fact]
    public async Task Concurrency_IsBounded()
    {
        var (manager, hub) = Create(concurrency: 1);
        hub.ChunkDelay = TimeSpan.FromMilliseconds(2);
        hub.Add("u/a", "m.onnx", 8000);
        hub.Add("u/b", "m.onnx", 8000);

        var a = manager.Enqueue(Request("u/a"));
        var b = manager.Enqueue(Request("u/b"));
        await Task.WhenAll(a.Completion, b.Completion);

        Assert.Equal(1, hub.MaxConcurrentTransfers);
    }

    [Fact]
    public async Task Enqueue_SameKeyWhileActive_ReturnsSameJob()
    {
        var (manager, hub) = Create();
        hub.ChunkDelay = TimeSpan.FromMilliseconds(2);
        hub.Add("u/a", "m.onnx", 10_000);

        var first = manager.Enqueue(Request("u/a"));
        var second = manager.Enqueue(Request("u/a"));
        await first.Completion;

        Assert.Same(first, second);
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public async Task Cancel_StopsJobAndRetryResumesPartialFile()
    {
        var (manager, hub) = Create();
        hub.ChunkDelay = TimeSpan.FromMilliseconds(3);
        hub.Add("u/a", "big.onnx", 60_000);

        var job = manager.Enqueue(Request("u/a"));
        while (job.DownloadedBytes < 10_000)
            await Task.Delay(5);
        manager.Cancel(job);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion);
        Assert.Equal(DownloadJobState.Cancelled, job.State);

        hub.ChunkDelay = TimeSpan.Zero;
        var retry = manager.Retry(job);
        var path = await retry.Completion;

        Assert.Contains(hub.RangeRequests, r => r.StartsWith("big.onnx@", StringComparison.Ordinal));
        Assert.Equal(hub.Content("u/a", "big.onnx"), File.ReadAllBytes(Path.Combine(path, "big.onnx")));
        Assert.Single(manager.Jobs); // the cancelled attempt was replaced
    }

    [Fact]
    public async Task CompleteFilesOnDisk_AreSkipped()
    {
        var (manager, hub) = Create();
        hub.Add("u/a", "m.onnx", 4000);
        hub.Add("u/a", "vocab.txt", 100);

        await manager.Enqueue(Request("u/a")).Completion;
        Assert.Equal(2, hub.FileRequests);
        manager.ClearFinished();
        var again = manager.Enqueue(Request("u/a"));
        await again.Completion;

        Assert.Equal(2, again.FilesDone);
        Assert.Equal(2, hub.FileRequests); // nothing fetched the second time
    }

    [Fact]
    public async Task Failure_IsReportedPerJobWithoutStoppingOthers()
    {
        var (manager, hub) = Create();
        hub.Add("u/ok", "m.onnx", 3000);
        hub.Add("u/bad", "m.onnx", 3000);
        hub.FailFile = "m.onnx";

        var bad = manager.Enqueue(Request("u/bad"));
        await Assert.ThrowsAnyAsync<Exception>(() => bad.Completion);
        Assert.Equal(DownloadJobState.Failed, bad.State);
        Assert.False(string.IsNullOrEmpty(bad.Error));

        hub.FailFile = null;
        var ok = manager.Enqueue(Request("u/ok"));
        await ok.Completion;
        Assert.Equal(DownloadJobState.Completed, ok.State);
        Assert.Equal(1, manager.Totals.Failed);
    }

    [Fact]
    public async Task SingleFile_DownloadsOnlyThatFile()
    {
        var (manager, hub) = Create();
        hub.Add("litert/gemma", "gemma.litertlm", 7000);
        hub.Add("litert/gemma", "gemma-gpu.litertlm", 9000);

        var request = new ModelDownloadRequest("translation", "Gemma", "litert/gemma", Path.Combine(_root, "translation"))
        {
            SingleFile = "gemma.litertlm",
            Kind = ModelDownloadKind.Translation,
        };
        var path = await manager.Enqueue(request).Completion;

        Assert.True(File.Exists(Path.Combine(path, "gemma.litertlm")));
        Assert.False(File.Exists(Path.Combine(path, "gemma-gpu.litertlm")));
    }

    [Fact]
    public void Catalog_RecommendsParakeetInt4()
    {
        var recommended = ModelCatalog.Recommended;
        Assert.Equal("Parakeet TDT 0.6B v3", recommended.CommercialName);
        Assert.Equal("int4", recommended.QuantizationFolder);
        Assert.Equal(ModelLatencyProfile.Streaming, recommended.Latency);
        Assert.Single(ModelCatalog.Models, m => m.IsRecommended);
    }
}
