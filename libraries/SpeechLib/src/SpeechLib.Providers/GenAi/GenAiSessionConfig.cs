using Microsoft.ML.OnnxRuntimeGenAI;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SpeechLib;

/// <summary>
/// Builds the ONNX Runtime GenAI <see cref="Config"/> for a model folder: selects the
/// execution provider and, on CPU, applies the thread/session tuning measured in
/// <c>OrtCpuTuning</c>.
/// </summary>
internal static class GenAiSessionConfig
{
    /// <summary>
    /// Creates a <see cref="Config"/> with execution-provider and search options applied.
    /// </summary>
    /// <param name="path">Model folder containing the GenAI config.</param>
    /// <param name="ep">Name of the execution provider to use.</param>
    /// <param name="searchOptions">Search options applied before the <c>Model</c> is constructed.</param>
    /// <param name="cpuThreads">Explicit intra-op thread count; derived from the core count when null.</param>
    /// <param name="sequentialExecution">Execution mode for the CPU session options.</param>
    public static Config Create(
        string path,
        string ep,
        GeneratorParamsArgs searchOptions,
        int? cpuThreads = null,
        bool? sequentialExecution = true)
    {
        var config = new Config(path);

        // WebGPU is a plugin EP: register it and select it by name. A request
        // for a GPU that is not present degrades to the CPU path below instead
        // of failing session creation (a persisted setting must never break
        // recognition).
        var webGpu = WebGpuRequest.TryParse(ep);
        if (webGpu is not null && !WebGpuExecutionProvider.TryApplyToGenAi(config, webGpu, out var webGpuError))
        {
            Console.WriteLine($"  Warning: WebGPU not applied ({webGpuError}); falling back to CPU.");
            webGpu = null;
            ep = "cpu";
        }

        if (ep == "cpu")
        {
            int threads = ResolveCpuThreads(cpuThreads);
            string? cpuOverlay = ModifyConfigForCpu(path, threads, sequentialExecution);
            if (!string.IsNullOrWhiteSpace(cpuOverlay))
            {
                config.Overlay(cpuOverlay);
                Console.WriteLine($"CPU config: applied overlay with intra_op={threads}");
            }
            else
            {
                // Fallback: set thread options directly via provider options
                Console.WriteLine($"CPU config: overlay failed, using provider options intra_op={threads}");
                config.SetProviderOption("cpu", "intra_op_num_threads", threads.ToString());
                config.SetProviderOption("cpu", "inter_op_num_threads", "1");
                config.SetProviderOption("cpu", "session.force_spinning_stop", "1");
                if (sequentialExecution.HasValue)
                {
                    config.SetProviderOption(
                        "cpu",
                        "session.execution_mode",
                        sequentialExecution.Value ? "ORT_SEQUENTIAL" : "ORT_PARALLEL");
                }
            }
        }

        if (webGpu is null && ep != "follow_config")
        {
            // DML: don't clear default providers (keep CPU fallback), just append DML
            if (ep == "dml")
            {
                Console.WriteLine($"Setting model to {ep} (keeping default CPU fallback)");
                config.AppendProvider(ep);
            }
            else if (ep == "tensorrt" || ep == "NvTensorRtRtx")
            {
                Console.WriteLine($"Setting model to NvTensorRtRtx (TensorRT)");
                config.AppendProvider("NvTensorRtRtx");
            }
            else
            {
                config.ClearProviders();
                if (ep != "cpu")
                {
                    Console.WriteLine($"Setting model to {ep}");
                    config.AppendProvider(ep);
                }
            }
        }

        // Create serializer context to skip null attributes
        var options = new JsonSerializerOptions()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var ctx = new ArgsSerializerContext(options);
        var json = JsonSerializer.Serialize(searchOptions, ctx.GeneratorParamsArgs);

        // Set any search-specific options that need to be known before constructing a Model object
        // Otherwise they can be set with generatorParams.SetSearchOptions(search_options)
        config.Overlay(json);
        return config;
    }

    /// <summary>
    /// Overlays CPU-optimized thread settings onto the genai_config.json.
    /// Calculates optimal intra_op_num_threads based on logical core count and injects
    /// session_options into every decoder/encoder sub-model section.
    /// </summary>
    /// <param name="path">Path to the model folder containing genai_config.json.</param>
    /// <param name="optimalIntraThreads">Intra-op thread count to inject.</param>
    /// <param name="sequentialExecution">Execution mode to inject, or null to keep the model default.</param>
    /// <returns>JSON overlay string with CPU thread settings, or null on failure.</returns>
    private static string? ModifyConfigForCpu(string path, int optimalIntraThreads, bool? sequentialExecution)
    {
        string configPath = Path.Combine(path, "genai_config.json");

        try
        {
            string jsonContent = File.ReadAllText(configPath);
            var rootNode = JsonNode.Parse(jsonContent);
            if (rootNode is null)
            {
                Console.WriteLine("CPU config: failed to parse genai_config.json (null root).");
                return null;
            }

            var modelNode = rootNode["model"];
            if (modelNode is null)
            {
                Console.WriteLine("CPU config: genai_config.json missing 'model' section.");
                return null;
            }

            // Inject session_options into every decoder/encoder sub-model
            foreach (var property in modelNode.AsObject())
            {
                if (property.Value is not JsonObject componentNode)
                    continue;

                if (property.Key != "decoder" && property.Key != "encoder")
                    continue;

                if (!componentNode.ContainsKey("session_options"))
                {
                    componentNode["session_options"] = new JsonObject();
                }

                var sessionOptions = componentNode["session_options"]!.AsObject();

                // Pin intra-op threads to physical P-cores for best ONNX throughput.
                // inter_op_num_threads = 1 avoids contention on CPU inference.
                // allow_spinning = 0 stops the workers busy-waiting between kernels:
                // with the encoder and decoder sessions both alive they otherwise
                // starve each other and the host threads (see OrtCpuTuning).
                sessionOptions["intra_op_num_threads"] = optimalIntraThreads;
                sessionOptions["inter_op_num_threads"] = 1;
                sessionOptions["session.intra_op.allow_spinning"] = "0";
                sessionOptions["session.inter_op.allow_spinning"] = "0";
                sessionOptions["session.force_spinning_stop"] = "1";
                if (sequentialExecution.HasValue)
                {
                    sessionOptions["execution_mode"] = sequentialExecution.Value
                        ? "ORT_SEQUENTIAL"
                        : "ORT_PARALLEL";
                }
            }

            string overlay = rootNode.ToJsonString();
            string executionMode = sequentialExecution switch
            {
                true => "ORT_SEQUENTIAL",
                false => "ORT_PARALLEL",
                _ => "default"
            };
            Console.WriteLine($"CPU config: intra_op_num_threads={optimalIntraThreads} " +
                              $"execution_mode={executionMode} " +
                              $"(logical cores={Environment.ProcessorCount}).");
            return overlay;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CPU config preparation failed: {ex.Message}");
            return null;
        }
    }

    private static int ResolveCpuThreads(int? cpuThreads)
    {
        if (cpuThreads is <= 0)
            throw new ArgumentOutOfRangeException(nameof(cpuThreads), cpuThreads, "CPU thread count must be positive.");

        return cpuThreads ?? ComputeOptimalIntraThreads(Environment.ProcessorCount);
    }

    /// <summary>
    /// Heuristic to pick the best intra_op_num_threads for ONNX CPU inference.
    /// On hybrid Intel CPUs (P-cores + E-cores) dividing by 2 targets physical
    /// P-cores only; on high-core parts threads are capped with headroom left for
    /// the other session and the host threads.
    /// </summary>
    /// <remarks>
    /// The upper branch used to cap at 8 threads on the assumption that LLM
    /// inference stops scaling there. That ceiling was measured with ONNX Runtime's
    /// worker spinning left on, which is what actually made extra threads harmful
    /// (see OrtCpuTuning). With spinning disabled, 12, 16 and 20 threads are all
    /// within noise of each other and about 2% faster than 8 on a 20-thread part,
    /// so the cap now leaves 8 threads of headroom instead of pinning to 8.
    /// </remarks>
    private static int ComputeOptimalIntraThreads(int logicalCores)
    {
        return logicalCores switch
        {
            <= 4  => Math.Max(1, logicalCores - 1),   // Low-end: leave 1 thread for OS
            <= 16 => logicalCores / 2,                 // Mid-range / hybrid: target P-cores
            <= 32 => Math.Max(8, logicalCores - 8),    // High-end: plateau, keep headroom
            _     => Math.Min(24, logicalCores - 8)    // 32+: scale with cores, capped
        };
    }
}
