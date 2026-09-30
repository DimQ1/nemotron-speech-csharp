using Microsoft.ML.OnnxRuntimeGenAI;
using System.Reflection;

namespace SpeechLib;

/// <summary>
/// Applies <see cref="GeneratorParamsArgs"/> to an ONNX Runtime GenAI
/// <see cref="GeneratorParams"/> instance during decoding.
/// </summary>
internal static class GenAiSearchOptions
{
    /// <summary>
    /// Sets the search options on a generator's params.
    /// </summary>
    /// <param name="generatorParams">Generator params object to set on.</param>
    /// <param name="args">Arguments provided by the caller.</param>
    /// <param name="verbose">Log the applied options to the console.</param>
    public static void Apply(GeneratorParams generatorParams, GeneratorParamsArgs args, bool verbose)
    {
        var type = args.GetType();
        var options = new List<string>();
        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var name = prop.Name;
            var value = prop.GetValue(args);
            if (value == null || name == "chunk_size") continue;

            if (name == "do_sample")
            {
                var val = Convert.ToBoolean(value);
                options.Add($"{name}: {val}");
                generatorParams.SetSearchOption(name, val);
            }
            else
            {
                var val = Convert.ToDouble(value);
                options.Add($"{name}: {val}");
                generatorParams.SetSearchOption(name, val);
            }
        }

        if (verbose) Console.WriteLine("GeneratorParams created: {" + string.Join(", ", options) + "}");
    }
}
