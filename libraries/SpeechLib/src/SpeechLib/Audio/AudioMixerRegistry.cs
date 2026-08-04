using SpeechLib.Models;

namespace SpeechLib.Audio;

/// <summary>
/// Registry that maps a live capture provider to its <see cref="IAudioMixer"/>.
/// Providers register their mixer at type initialization; applications resolve
/// the mixer that matches the configured <see cref="IAudioSourceFactory"/>
/// without referencing concrete provider types.
/// </summary>
public static class AudioMixerRegistry
{
    private static readonly Dictionary<Type, IAudioMixer> _mixers = new();
    private static readonly object _gate = new();

    /// <summary>Registers (or replaces) the mixer for a given factory type.</summary>
    public static void Register<TFactory>(IAudioMixer mixer) where TFactory : IAudioSourceFactory
    {
        ArgumentNullException.ThrowIfNull(mixer);
        lock (_gate)
            _mixers[typeof(TFactory)] = mixer;
    }

    /// <summary>
    /// Resolves the mixer for the given factory instance.
    /// Throws when the provider did not register a mixer.
    /// </summary>
    public static IAudioMixer For(IAudioSourceFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_mixers.TryGetValue(factory.GetType(), out var mixer))
                return mixer;
        }

        throw new InvalidOperationException(
            $"No audio mixer registered for capture provider '{factory.GetType().Name}'. " +
            "The provider assembly must be loaded and register its mixer via AudioMixerRegistry.");
    }

    /// <summary>Returns the mixer for the given factory, or <c>null</c> when none is registered.</summary>
    public static IAudioMixer? TryFor(IAudioSourceFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
            return _mixers.TryGetValue(factory.GetType(), out var mixer) ? mixer : null;
    }
}
