using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// Loads each model variant once for all model tests (a load reads 0.7 to 2.5 GB). Tests take the recognizer, reset it
/// and hand it back in a clean state; the collection keeps them sequential. At most two variants stay loaded (the
/// least recently used is disposed), so the four variants fit in the memory of a shared machine.
/// </summary>
public sealed class NemotronModelCache : IDisposable
{
    private const int MaxLoaded = 2;

    private readonly Dictionary<string, NemotronRnntRecognizer> _recognizers = new();
    private readonly List<string> _recentlyUsed = new();   // oldest first

    /// <summary>
    /// Disposes every cached model. A test that loads recognizers of its own (other options, the product factory, a failing
    /// load) calls this first, so the cache's models and the test's never add up on a machine with little free memory;
    /// the next <see cref="Get"/> loads again.
    /// </summary>
    public void ReleaseAll()
    {
        foreach (var recognizer in _recognizers.Values)
            recognizer.Dispose();
        _recognizers.Clear();
        _recentlyUsed.Clear();
    }

    internal NemotronRnntRecognizer Get(string variant)
    {
        if (!_recognizers.TryGetValue(variant, out var recognizer))
        {
            while (_recognizers.Count >= MaxLoaded)
            {
                var oldest = _recentlyUsed[0];
                _recentlyUsed.RemoveAt(0);
                _recognizers[oldest].Dispose();
                _recognizers.Remove(oldest);
            }

            var directory = NemotronTestEnvironment.LocateModel(variant)
                ?? throw new InvalidOperationException($"Nemotron model '{variant}' not found.");
            recognizer = new NemotronRnntRecognizer(directory, "cpu", langId: null, useVad: false,
                new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads });
            _recognizers[variant] = recognizer;
        }
        _recentlyUsed.Remove(variant);
        _recentlyUsed.Add(variant);

        // every test starts from the defaults: new stream, English, no silence drop, the folder's blank penalty
        recognizer.TrySetVad(false);
        recognizer.TrySetLanguage("0");
        recognizer.BlankPenalty = recognizer.Config.BlankPenalty;
        recognizer.ResetStreamingState();
        return recognizer;
    }

    public void Dispose() => ReleaseAll();
}

[CollectionDefinition(Name)]
public sealed class NemotronModelCollection : ICollectionFixture<NemotronModelCache>
{
    public const string Name = "Nemotron models";
}
