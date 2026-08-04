using System.Reflection;
using System.Runtime.Loader;
using LocalVoiceAssistant.Maui.Plugins;

namespace LocalVoiceAssistant.Maui.Core;

public interface IPluginHost
{
    IReadOnlyList<IAssistantPlugin> Plugins { get; }
    bool HasPermission(IAssistantPlugin plugin, PluginPermission permission);
    Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        IProgress<PluginProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class PluginHost : IPluginHost
{
    private readonly Dictionary<string, IAssistantPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PluginPermission> _allowedPermissions;

    public PluginHost(IEnumerable<IAssistantPlugin> plugins, IEnumerable<PluginPermission>? allowedPermissions = null)
    {
        _allowedPermissions = allowedPermissions is null
            ? Enum.GetValues<PluginPermission>().ToHashSet()
            : allowedPermissions.ToHashSet();

        foreach (var plugin in plugins)
            Register(plugin);
    }

    public IReadOnlyList<IAssistantPlugin> Plugins => _plugins.Values.OrderBy(plugin => plugin.Manifest.Name).ToArray();

    public void Register(IAssistantPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (string.IsNullOrWhiteSpace(plugin.Manifest.Id))
            throw new InvalidOperationException("A plugin manifest must define a non-empty id.");
        if (!_plugins.TryAdd(plugin.Manifest.Id, plugin))
            throw new InvalidOperationException($"Plugin '{plugin.Manifest.Id}' is already registered.");
    }

    public bool HasPermission(IAssistantPlugin plugin, PluginPermission permission) =>
        plugin.Manifest.Permissions.Contains(permission) && _allowedPermissions.Contains(permission);

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        IProgress<PluginProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_plugins.TryGetValue(invocation.PluginId, out var plugin))
            return ToolResult.Failure($"Плагин '{invocation.PluginId}' не найден.", "plugin_not_found");

        PluginPermission? missingPermission = null;
        foreach (var permission in plugin.Manifest.Permissions)
        {
            if (!_allowedPermissions.Contains(permission))
            {
                missingPermission = permission;
                break;
            }
        }

        if (missingPermission is not null)
            return ToolResult.Failure(
                $"Плагин «{plugin.Manifest.Name}» требует разрешение «{missingPermission.Value}».",
                "permission_denied");

        return await plugin.InvokeAsync(invocation, progress, cancellationToken).ConfigureAwait(false);
    }

    public static int LoadPluginsFromDirectory(PluginHost host, string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        var loaded = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            foreach (var type in assembly.GetTypes().Where(type =>
                         !type.IsAbstract && typeof(IAssistantPlugin).IsAssignableFrom(type)))
            {
                if (Activator.CreateInstance(type) is IAssistantPlugin plugin)
                {
                    host.Register(plugin);
                    loaded++;
                }
            }
        }

        return loaded;
    }
}
