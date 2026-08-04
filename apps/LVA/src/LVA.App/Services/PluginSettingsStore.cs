using LVA.Plugins.Abstractions;

namespace LVA.App.Services;

public interface IPluginSettingsStore
{
    Task<string> GetAsync(string pluginId, PluginSettingField field, CancellationToken ct = default);
    Task SetAsync(string pluginId, PluginSettingField field, string value, CancellationToken ct = default);
}

/// <summary>Persists ordinary settings locally and secrets in the OS credential store.</summary>
public sealed class PluginSettingsStore : IPluginSettingsStore
{
    private readonly ISecretStore _secrets;

    public PluginSettingsStore(ISecretStore secrets) => _secrets = secrets;

    public async Task<string> GetAsync(string pluginId, PluginSettingField field, CancellationToken ct = default)
    {
        var key = Key(pluginId, field.Key);
        if (field.Secret)
            return await _secrets.GetAsync(key, ct) ?? string.Empty;

        return Preferences.Get(key, field.DefaultValue);
    }

    public async Task SetAsync(string pluginId, PluginSettingField field, string value, CancellationToken ct = default)
    {
        var key = Key(pluginId, field.Key);
        if (field.Secret)
            await _secrets.SetAsync(key, value, ct);
        else
            Preferences.Set(key, value);
    }

    public static string Key(string pluginId, string settingKey)
        => $"lva.plugin.{pluginId}.{settingKey}";
}