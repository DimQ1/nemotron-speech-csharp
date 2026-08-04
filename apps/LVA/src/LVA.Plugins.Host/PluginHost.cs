using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using LVA.Plugins.Abstractions;

namespace LVA.Plugins.Host;

/// <summary>
/// Plugin host (TZ §6.2): discovery by *.lva-plugin.json manifests, loading in
/// a separate AssemblyLoadContext, permission enforcement (no HttpClient
/// without InternetAccess).
/// </summary>
public sealed class PluginHost
{
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly ISecretStore _secretStore;
    private readonly IHttpClientFactory _httpClientFactory;

    public PluginHost(IHttpClientFactory httpClientFactory, ISecretStore secretStore)
    {
        _httpClientFactory = httpClientFactory;
        _secretStore = secretStore;
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>Discovers and loads all plugins from a directory.</summary>
    public async Task LoadDirectoryAsync(string pluginDir, IReadOnlyDictionary<string, string>? settings = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(pluginDir))
            return;

        foreach (var manifestPath in Directory.EnumerateFiles(pluginDir, "*.lva-plugin.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath, ct));
            if (manifest is null)
                continue;

            var plugin = LoadPlugin(manifest, Path.GetDirectoryName(manifestPath)!, settings ?? new Dictionary<string, string>());
            if (plugin is not null)
                _plugins.Add(plugin);
        }
    }

    /// <summary>Registers an already-instantiated plugin (e.g. compiled-in defaults).</summary>
    public async Task RegisterAsync(IAssistantPlugin plugin, IReadOnlyDictionary<string, string>? settings = null, CancellationToken ct = default)
    {
        var context = CreateContext(plugin.Manifest, settings ?? new Dictionary<string, string>());
        await plugin.InitAsync(context, ct);
        _plugins.Add(new LoadedPlugin(plugin, null));
    }

    private LoadedPlugin? LoadPlugin(PluginManifest manifest, string dir, IReadOnlyDictionary<string, string> settings)
    {
        if (manifest.Assembly is null || manifest.EntryType is null)
            return null;

        var assemblyPath = Path.Combine(dir, manifest.Assembly);
        if (!File.Exists(assemblyPath))
            return null;

        var alc = new AssemblyLoadContext($"lva-plugin-{manifest.Id}", isCollectible: true);
        alc.Resolving += (context, name) =>
        {
            // Resolve plugin-local dependencies first.
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        var assembly = alc.LoadFromAssemblyPath(assemblyPath);
        var type = assembly.GetType(manifest.EntryType)
            ?? throw new InvalidOperationException($"Entry type '{manifest.EntryType}' not found in {assemblyPath}");

        if (Activator.CreateInstance(type) is not IAssistantPlugin plugin)
            throw new InvalidOperationException($"Type '{manifest.EntryType}' does not implement IAssistantPlugin");

        var context2 = CreateContext(manifest, settings);
        plugin.InitAsync(context2, CancellationToken.None).GetAwaiter().GetResult();
        return new LoadedPlugin(plugin, alc);
    }

    private PluginContext CreateContext(PluginManifest manifest, IReadOnlyDictionary<string, string> settings)
        => new(manifest, manifest.Permissions.HasFlag(PluginPermissions.InternetAccess) ? _httpClientFactory : null,
               _secretStore, settings);

    public sealed record LoadedPlugin(IAssistantPlugin Plugin, AssemblyLoadContext? LoadContext);

    private sealed class PluginContext : IPluginContext
    {
        private readonly IHttpClientFactory? _httpFactory;

        public PluginContext(PluginManifest manifest, IHttpClientFactory? httpFactory,
            ISecretStore secrets, IReadOnlyDictionary<string, string> settings)
        {
            Manifest = manifest;
            _httpFactory = httpFactory;
            Secrets = secrets;
            Settings = settings;
        }

        public PluginManifest Manifest { get; }

        public IHttpClientFactory HttpFactory => _httpFactory
            ?? throw new InvalidOperationException(
                $"Plugin '{Manifest.Id}' requested HttpClient without declaring InternetAccess permission.");

        public ISecretStore Secrets { get; }
        public IReadOnlyDictionary<string, string> Settings { get; }
    }
}
