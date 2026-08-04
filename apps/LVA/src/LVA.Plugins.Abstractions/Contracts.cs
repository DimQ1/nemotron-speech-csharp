using System.Text.Json.Serialization;

namespace LVA.Plugins.Abstractions;

/// <summary>Permissions declared by a plugin; shown to the user on install (TZ §6.2).</summary>
[Flags]
public enum PluginPermissions
{
    None = 0,
    InternetAccess = 1,
    CloudModels = 2,
    LocalFiles = 4,
    Microphone = 8
}

/// <summary>Plugin manifest (deserialized from *.lva-plugin.json).</summary>
public sealed record PluginManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("capabilities")] string[] Capabilities,
    [property: JsonPropertyName("permissions")] PluginPermissions Permissions,
    [property: JsonPropertyName("assembly")] string? Assembly = null,
    [property: JsonPropertyName("entryType")] string? EntryType = null,
    [property: JsonPropertyName("settings")] PluginSettingsDefinition? Settings = null);

/// <summary>Declarative settings schema rendered in a dedicated plugin window.</summary>
public sealed record PluginSettingsDefinition(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("fields")] IReadOnlyList<PluginSettingField> Fields);

/// <summary>A single plugin setting. Secret values are kept in OS secure storage.</summary>
public sealed record PluginSettingField(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("defaultValue")] string DefaultValue = "",
    [property: JsonPropertyName("secret")] bool Secret = false,
    [property: JsonPropertyName("hint")] string? Hint = null);

/// <summary>A routed call from the orchestrator to a plugin.</summary>
public sealed record ToolInvocation(
    string Capability,
    string Context,
    IReadOnlyDictionary<string, string> Arguments);

/// <summary>Terminal result of a plugin execution.</summary>
public sealed record ToolResult(
    bool Success,
    string? ResultMarkdown = null,
    string? Error = null,
    IReadOnlyList<string>? Sources = null)
{
    public static ToolResult Ok(string markdown, IReadOnlyList<string>? sources = null)
        => new(true, markdown, null, sources);

    public static ToolResult Fail(string error) => new(false, null, error, null);
}

/// <summary>Intermediate progress reported during execution.</summary>
public sealed record TaskProgress(int Percent, string Message);

/// <summary>What the core gives a plugin (TZ §6.1).</summary>
public interface IPluginContext
{
    /// <summary>HTTP factory — issued ONLY when Permissions.InternetAccess is declared.</summary>
    IHttpClientFactory HttpFactory { get; }

    /// <summary>Plugin settings from configuration.</summary>
    IReadOnlyDictionary<string, string> Settings { get; }

    /// <summary>Secret store for API keys (OS-backed in the app).</summary>
    ISecretStore Secrets { get; }
}

/// <summary>Secret storage abstraction; app implements over OS credential storage.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Plugin contract — the only dependency of plugin assemblies (TZ §6.1).</summary>
public interface IAssistantPlugin
{
    PluginManifest Manifest { get; }

    Task InitAsync(IPluginContext context, CancellationToken ct);

    Task<ToolResult> ExecuteAsync(
        ToolInvocation call,
        IProgress<TaskProgress> progress,
        CancellationToken ct);
}
