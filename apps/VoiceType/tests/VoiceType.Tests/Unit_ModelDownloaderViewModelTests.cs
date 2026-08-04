using VoiceType.Services;
using VoiceType.ViewModels;
using Xunit;
using VoiceType.Models;
using SpeechLib.Audio;
using System.Net;
using System.Text;

namespace VoiceType.Tests;

/// <summary>
/// Unit tests for ModelDownloaderViewModel — testable parts that don't need WPF dispatcher.
/// </summary>
public sealed class Unit_ModelDownloaderViewModelTests
{
    [Fact]
    public void AppSettings_DefaultNumBeams_IsCpuFriendly()
    {
        var settings = new AppSettings();

        Assert.Equal(1, settings.NumBeams);
    }

    [Fact]
    public void SettingsViewModel_BuildSettings_PreservesQualityAndDownloaderSettings()
    {
        var settings = new AppSettings
        {
            ModelsRootPath = Path.Combine(Path.GetTempPath(), $"VoiceType_missing_{Guid.NewGuid():N}"),
            NumBeams = 2,
            RepetitionPenalty = 1.25,
            DownloaderRepoId = "org/repo",
            DownloaderModelsRootPath = @"C:\Models",
            DownloaderSelectedFoldersRepoId = "org/repo",
            DownloaderSelectedFolders = ["cpu", "tokenizer"]
        };

        var viewModel = new SettingsViewModel(settings);
        var saved = viewModel.BuildSettings();

        Assert.Equal(2, saved.NumBeams);
        Assert.Equal(1.25, saved.RepetitionPenalty);
        Assert.Equal("org/repo", saved.DownloaderRepoId);
        Assert.Equal(@"C:\Models", saved.DownloaderModelsRootPath);
        Assert.Equal("org/repo", saved.DownloaderSelectedFoldersRepoId);
        Assert.Equal(["cpu", "tokenizer"], saved.DownloaderSelectedFolders);
    }

    [Fact]
    public void AudioUtils_Convert_ConvertsPcm16ToFloatSamples()
    {
        var pcm = new short[] { short.MinValue, 0, short.MaxValue };
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        var samples = AudioUtils.Convert(bytes, bytes.Length, new NAudio.Wave.WaveFormat(16000, 16, 1));

        Assert.Equal(-1f, samples[0]);
        Assert.Equal(0f, samples[1]);
        Assert.InRange(samples[2], 0.9999f, 1f);
    }

    [Fact]
    public void AudioUtils_Resample_ReturnsArrayWithoutExtraSamples()
    {
        var samples = AudioUtils.Resample([0f, 10f, 20f, 30f], fromRate: 4, toRate: 2);

        Assert.Equal(2, samples.Length);
        Assert.Equal(0f, samples[0]);
        Assert.Equal(20f, samples[1]);
    }

    [Fact]
    public void ResolveDownloaderRepoId_UsesPersistedValue_WhenPresent()
    {
        var settings = new AppSettings { DownloaderRepoId = "org/custom-repo" };

        var repoId = ModelDownloaderViewModel.ResolveDownloaderRepoId(settings);

        Assert.Equal("org/custom-repo", repoId);
    }

    [Fact]
    public void ResolveDownloaderRepoId_FallsBackToDefault_WhenEmpty()
    {
        var repoId = ModelDownloaderViewModel.ResolveDownloaderRepoId(new AppSettings());

        Assert.Equal("DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu", repoId);
    }

    [Fact]
    public void ResolveDownloaderModelsRootPath_PrefersDownloaderSetting()
    {
        var settings = new AppSettings
        {
            ModelsRootPath = @"C:\EngineModels",
            DownloaderModelsRootPath = @"D:\DownloadModels"
        };

        var path = ModelDownloaderViewModel.ResolveDownloaderModelsRootPath(settings);

        Assert.Equal(@"D:\DownloadModels", path);
    }

    [Fact]
    public void ResolveDownloaderModelsRootPath_FallsBackToEngineModelsRootPath()
    {
        var settings = new AppSettings { ModelsRootPath = @"C:\EngineModels" };

        var path = ModelDownloaderViewModel.ResolveDownloaderModelsRootPath(settings);

        Assert.Equal(@"C:\EngineModels", path);
    }

    [Fact]
    public void PersistDownloaderSettings_WritesRepoAndPath()
    {
        var settings = new AppSettings();

        ModelDownloaderViewModel.PersistDownloaderSettings(settings, " org/repo ", " C:\\Models ");

        Assert.Equal("org/repo", settings.DownloaderRepoId);
        Assert.Equal("C:\\Models", settings.DownloaderModelsRootPath);
    }

    [Fact]
    public void PersistDownloaderFolderSelection_WritesRepoAndFolderKeys()
    {
        var settings = new AppSettings();

        ModelDownloaderViewModel.PersistDownloaderFolderSelection(settings, " org/repo ", ["cpu", "tokenizer", "cpu"]);

        Assert.Equal("org/repo", settings.DownloaderSelectedFoldersRepoId);
        Assert.Equal(["cpu", "tokenizer"], settings.DownloaderSelectedFolders);
    }

    [Fact]
    public void ResolveDownloaderSelectedFolders_ReadsPersistedKeys()
    {
        var settings = new AppSettings { DownloaderSelectedFolders = ["cpu", "tokenizer"] };

        var selected = ModelDownloaderViewModel.ResolveDownloaderSelectedFolders(settings);

        Assert.Contains("cpu", selected);
        Assert.Contains("tokenizer", selected);
    }

    [Fact]
    public void ApplySelectedFolders_RestoresSelectionBySubfolder()
    {
        var folders = new List<HfFolder>
        {
            new() { Name = "📁 cpu", Selected = true },
            new() { Name = "📁 tokenizer", Selected = true },
            new() { Name = "📄 Root files", Selected = true }
        };

        ModelDownloaderViewModel.ApplySelectedFolders(folders, new HashSet<string>(["tokenizer", string.Empty], StringComparer.OrdinalIgnoreCase));

        Assert.False(folders[0].Selected);
        Assert.True(folders[1].Selected);
        Assert.True(folders[2].Selected);
    }

    [Fact]
    public void CaptureSelectedFolderKeys_ReturnsOnlySelectedFolders()
    {
        var selected = ModelDownloaderViewModel.CaptureSelectedFolderKeys(
        [
            new HfFolder { Name = "📁 cpu", Selected = true },
            new HfFolder { Name = "📁 tokenizer", Selected = false },
            new HfFolder { Name = "📄 Root files", Selected = true }
        ]);

        Assert.Contains("cpu", selected);
        Assert.Contains(string.Empty, selected);
        Assert.DoesNotContain("tokenizer", selected);
    }

    [Fact]
    public void HfFolder_SubfolderName_ForRootFiles_IsEmpty()
    {
        var folder = new HfFolder { Name = "📄 Root files" };

        Assert.Equal(string.Empty, folder.SubfolderName);
    }

    [Fact]
    public void TryResolveCustomResultModelPath_SingleRootFolder_UsesRepoSlug()
    {
        var path = ModelDownloaderViewModel.TryResolveCustomResultModelPath(
            [new HfFolder { Name = "📄 Root files", Selected = true }],
            "DimQ1/sample-repo",
            @"C:\Models");

        Assert.Equal(Path.Combine(@"C:\Models", "sample-repo"), path);
    }

    [Fact]
    public void TryResolveCustomResultModelPath_MultipleFolders_ReturnsNull()
    {
        var path = ModelDownloaderViewModel.TryResolveCustomResultModelPath(
            [
                new HfFolder { Name = "📁 cpu", Selected = true },
                new HfFolder { Name = "📁 tokenizer", Selected = true }
            ],
            "DimQ1/sample-repo",
            @"C:\Models");

        Assert.Null(path);
    }

    [Fact]
    public void AudioRecorderService_StopAndSave_WithoutSamples_ReturnsNull()
    {
        using var recorder = new AudioRecorderService(16000);
        recorder.Start();

        var outputBasePath = Path.Combine(Path.GetTempPath(), $"VoiceType_empty_{Guid.NewGuid():N}");
        var result = recorder.StopAndSave(outputBasePath);

        Assert.Null(result);
        Assert.False(File.Exists(Path.ChangeExtension(outputBasePath, ".mp3")));
    }

    [Fact]
    public async Task AudioRecorderService_StopAndSave_WithSamples_WritesMp3()
    {
        using var recorder = new AudioRecorderService(16000);
        recorder.Start();

        var samples = Enumerable.Range(0, 4000)
            .Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / 16000))
            .ToArray();
        await recorder.AppendAsync(samples);

        var outputBasePath = Path.Combine(Path.GetTempPath(), $"VoiceType_audio_{Guid.NewGuid():N}");
        string? result = null;

        try
        {
            result = recorder.StopAndSave(outputBasePath);

            Assert.NotNull(result);
            Assert.True(File.Exists(result));
            Assert.True(new FileInfo(result).Length > 0);
        }
        finally
        {
            if (!string.IsNullOrEmpty(result) && File.Exists(result))
                File.Delete(result);
        }
    }

    [Fact]
    public void PostProcessingPipeline_CompileRules_SkipsMalformedRegex()
    {
        var rules = new List<PostProcessingRule>
        {
            new() { Pattern = "[", Replacement = "", Enabled = true },
            new() { Pattern = "foo", Replacement = "bar", Enabled = true }
        };

        var compiled = PostProcessingPipeline.CompileRules(rules, enabled: true);

        Assert.Single(compiled);
        Assert.Equal("bar", compiled[0].Replacement);
    }

    [Fact]
    public void PostProcessingPipeline_Process_WithCompiledRules_AppliesAllRules()
    {
        var rules = new List<PostProcessingRule>
        {
            new() { Pattern = "<auto>", Replacement = "", Enabled = true },
        };

        var compiled = PostProcessingPipeline.CompileRules(rules, enabled: true);
        var processed = PostProcessingPipeline.Process("  hello   <auto>  world  ", compiled);

        // Process collapses whitespace but does NOT trim (preserves chunk boundaries for streaming)
        Assert.Equal(" hello world ", processed);
    }

    [Fact]
    public void PostProcessingPipeline_ProcessFinal_TrimsResult()
    {
        var rules = new List<PostProcessingRule>
        {
            new() { Pattern = "<auto>", Replacement = "", Enabled = true },
        };

        var compiled = PostProcessingPipeline.CompileRules(rules, enabled: true);
        var final = PostProcessingPipeline.ProcessFinal("  hello   <auto>  world  ", compiled);

        Assert.Equal("hello world", final);
    }

    // ── ParseRepoId ─────────────────────────────────────────────

    [Theory]
    [InlineData("DimQ1/nemotron-speech-onnx", "DimQ1/nemotron-speech-onnx")]
    [InlineData("DimQ1/nemotron-speech-onnx/", "DimQ1/nemotron-speech-onnx")]
    [InlineData("https://huggingface.co/DimQ1/nemotron-speech-onnx", "DimQ1/nemotron-speech-onnx")]
    [InlineData("https://huggingface.co/DimQ1/nemotron-speech-onnx/", "DimQ1/nemotron-speech-onnx")]
    [InlineData("huggingface.co/DimQ1/nemotron-speech-onnx", "DimQ1/nemotron-speech-onnx")]
    [InlineData("https://huggingface.co/DimQ1/nemotron-speech-onnx/tree/main/cpu", "DimQ1/nemotron-speech-onnx")]
    [InlineData("  DimQ1/nemotron-speech-onnx  ", "DimQ1/nemotron-speech-onnx")]
    public void ParseRepoId_ShouldReturnBaseRepoId(string input, string expected)
    {
        Assert.Equal(expected, ModelDownloaderViewModel.ParseRepoId(input));
    }

    [Fact]
    public void ParseRepoId_LongSlug_ReturnsFirstTwoSegments()
    {
        Assert.Equal("org/my-repo", ModelDownloaderViewModel.ParseRepoId("org/my-repo/something/else"));
    }

    // ── HfFolder model ──────────────────────────────────────────

    [Fact]
    public void HfFolder_TotalSize_ReturnsSumOfFiles()
    {
        var folder = new HfFolder
        {
            Files = new List<HfFile>
            {
                new() { SizeBytes = 100 },
                new() { SizeBytes = 200 },
                new() { SizeBytes = 300 }
            }
        };
        Assert.Equal(600, folder.TotalSize);
    }

    [Fact]
    public void HfFolder_SizeDisplay_UsesFormatSize()
    {
        var folder = new HfFolder { Files = new List<HfFile> { new() { SizeBytes = 1_500_000 } } };
        Assert.Equal("1.5 MB", folder.SizeDisplay);
    }

    [Fact]
    public void HfFolder_EmptyFiles_HasZeroSize()
    {
        var folder = new HfFolder();
        Assert.Equal(0, folder.TotalSize);
        Assert.Equal("0 B", folder.SizeDisplay);
    }

    [Fact]
    public void HfFolder_Selected_RaisesPropertyChanged()
    {
        var folder = new HfFolder();
        var changed = false;
        folder.PropertyChanged += (_, args) => changed = args.PropertyName == nameof(HfFolder.Selected);

        folder.Selected = false;

        Assert.True(changed);
    }

    // ── DownloadProgress model ──────────────────────────────────

    [Fact]
    public void DownloadProgress_HoldsValues()
    {
        var p = new DownloadProgress
        {
            CurrentFile = "cpu/tokenizer.json",
            FileProgress = 75.5,
            OverallProgress = 42.0,
            DownloadedFiles = 3,
            TotalFiles = 7
        };
        Assert.Equal("cpu/tokenizer.json", p.CurrentFile);
        Assert.Equal(75.5, p.FileProgress);
        Assert.Equal(42.0, p.OverallProgress);
        Assert.Equal(3, p.DownloadedFiles);
        Assert.Equal(7, p.TotalFiles);
    }

    // ── AsyncRelayCommand ───────────────────────────────────────

    [Fact]
    public void AsyncRelayCommand_CanExecute_WhenNotRunning()
    {
        var cmd = new AsyncRelayCommand(() => Task.CompletedTask, () => true);
        Assert.True(cmd.CanExecute(null));
    }

    [Fact]
    public void AsyncRelayCommand_CannotExecute_WhenCanExecuteFalse()
    {
        var cmd = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
        Assert.False(cmd.CanExecute(null));
    }

    [Fact]
    public async Task AsyncRelayCommand_ExecutesTask()
    {
        var tcs = new TaskCompletionSource<bool>();
        var cmd = new AsyncRelayCommand(async () =>
        {
            await Task.Yield();
            tcs.SetResult(true);
        });
        cmd.Execute(null);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task;
        Assert.True(completed, "Async command did not complete within 2 seconds");
    }

    [Fact]
    public async Task ModelDownloaderService_DownloadSingleFile_WritesFile_AndRaisesCompleted()
    {
        await using var server = await TestHttpServer.Start(async context =>
        {
            var payload = Encoding.UTF8.GetBytes("hello downloader");
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });

        using var downloader = new ModelDownloaderService();
        bool? completedOk = null;
        string? completedMessage = null;
        downloader.Completed += (ok, msg) => { completedOk = ok; completedMessage = msg; };

        var outputDir = Path.Combine(Path.GetTempPath(), $"VoiceType_downloader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var destPath = Path.Combine(outputDir, "payload.txt");

        try
        {
            await downloader.DownloadSingleFile(server.Url + "file.txt", destPath);

            Assert.True(File.Exists(destPath));
            Assert.Equal("hello downloader", await File.ReadAllTextAsync(destPath));
            Assert.False(downloader.IsDownloading);
            Assert.True(completedOk);
            Assert.Equal(destPath, completedMessage);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task ModelDownloaderService_DownloadSingleFile_OnFailure_ResetsState_AndRaisesCompleted()
    {
        await using var server = await TestHttpServer.Start(context =>
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return Task.CompletedTask;
        });

        using var downloader = new ModelDownloaderService();
        bool? completedOk = null;
        string? completedMessage = null;
        downloader.Completed += (ok, msg) => { completedOk = ok; completedMessage = msg; };

        var outputDir = Path.Combine(Path.GetTempPath(), $"VoiceType_downloader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var destPath = Path.Combine(outputDir, "missing.txt");

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                downloader.DownloadSingleFile(server.Url + "missing.txt", destPath));

            Assert.False(downloader.IsDownloading);
            Assert.False(completedOk);
            Assert.False(string.IsNullOrWhiteSpace(completedMessage));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task ModelDownloaderService_FetchRepoFolders_GroupsHuggingFaceSiblings()
    {
        await using var server = await TestHttpServer.Start(context =>
        {
            if (context.Request.Url?.AbsolutePath == "/api/models/org/repo")
            {
                var payload = Encoding.UTF8.GetBytes("""
                { "siblings": [
                    { "rfilename": "cpu/encoder.onnx", "size": 3 },
                    { "rfilename": "cpu/tokenizer.json", "size": 5 },
                    { "rfilename": "gpu/encoder.onnx", "size": 7 },
                    { "rfilename": "README.md", "size": 11 },
                    { "rfilename": ".gitattributes", "size": 1 }
                ]}
                """);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                return context.Response.OutputStream.WriteAsync(payload).AsTask()
                    .ContinueWith(_ => context.Response.Close());
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
            return Task.CompletedTask;
        });

        using var httpClient = new HttpClient();
        using var downloader = new ModelDownloaderService(httpClient, server.Url.TrimEnd('/'));

        var folders = await downloader.FetchRepoFolders("org/repo");

        Assert.Equal(3, folders.Count);
        Assert.Equal("📄 Root files", folders[0].Name);
        Assert.Equal("📁 cpu", folders[1].Name);
        Assert.Equal("📁 gpu", folders[2].Name);
        Assert.Equal(2, folders[1].Files.Count);
    }

    [Fact]
    public async Task ModelDownloaderService_DownloadFromHuggingFace_WritesSelectedFolders_AndReportsCurrentFileProgress()
    {
        await using var server = await TestHttpServer.Start(async context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (path == "/org/repo/resolve/main/cpu/encoder.onnx")
            {
                await WriteResponse(context, "enc");
                return;
            }

            if (path == "/org/repo/resolve/main/cpu/tokenizer.json")
            {
                await WriteResponse(context, "token");
                return;
            }

            if (path == "/org/repo/resolve/main/gpu/encoder.onnx")
            {
                await WriteResponse(context, "gpu");
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        });

        using var httpClient = new HttpClient();
        using var downloader = new ModelDownloaderService(httpClient, server.Url.TrimEnd('/'));
        var progressFiles = new List<string>();
        bool? completedOk = null;

        downloader.ProgressChanged += progress => progressFiles.Add(progress.CurrentFile);
        downloader.Completed += (ok, _) => completedOk = ok;

        var folders = new List<HfFolder>
        {
            new()
            {
                Name = "📁 cpu",
                Selected = true,
                Files =
                [
                    new HfFile { Name = "encoder.onnx", RelativePath = "cpu/encoder.onnx", SizeBytes = 3 },
                    new HfFile { Name = "tokenizer.json", RelativePath = "cpu/tokenizer.json", SizeBytes = 5 }
                ]
            },
            new()
            {
                Name = "📁 gpu",
                Selected = false,
                Files = [new HfFile { Name = "encoder.onnx", RelativePath = "gpu/encoder.onnx", SizeBytes = 3 }]
            }
        };

        var outputDir = Path.Combine(Path.GetTempPath(), $"VoiceType_hf_{Guid.NewGuid():N}");
        try
        {
            await downloader.DownloadFromHuggingFace("org/repo", folders, outputDir);

            Assert.True(completedOk);
            Assert.Equal("enc", await File.ReadAllTextAsync(Path.Combine(outputDir, "cpu", "encoder.onnx")));
            Assert.Equal("token", await File.ReadAllTextAsync(Path.Combine(outputDir, "cpu", "tokenizer.json")));
            Assert.False(File.Exists(Path.Combine(outputDir, "gpu", "encoder.onnx")));
            Assert.Contains("cpu/encoder.onnx", progressFiles);
            Assert.Contains("cpu/tokenizer.json", progressFiles);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }

        static async Task WriteResponse(HttpListenerContext context, string text)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }
    }

    private sealed class TestHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<HttpListenerContext, Task> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;

        public string Url { get; }

        private TestHttpServer(HttpListener listener, string url, Func<HttpListenerContext, Task> handler)
        {
            _listener = listener;
            Url = url;
            _handler = handler;
            _listener.Start();
            _loopTask = Task.Run(ListenLoopAsync);
        }

        public static async Task<TestHttpServer> Start(Func<HttpListenerContext, Task> handler)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var url = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            var server = new TestHttpServer(listener, url, handler);
            await Task.Delay(50);
            return server;
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    await _handler(context);
                }
                catch (HttpListenerException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    if (context is not null)
                    {
                        try
                        {
                            context.Response.StatusCode = 500;
                            context.Response.Close();
                        }
                        catch { }
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try { await _loopTask; } catch { }
            _cts.Dispose();
        }
    }
}
