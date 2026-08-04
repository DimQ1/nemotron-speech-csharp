using LVA.App.Companion;
using LVA.App.Services;
using LVA.App.ViewModels;
using LVA.Core.Configuration;
using LVA.Core.History;
using LVA.Core.Intents;
using LVA.Nlu.Intents;
using LVA.Orchestration;
using LVA.Plugins.Abstractions;
using LVA.Plugins.Host;
using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;

#if WINDOWS
using LVA.App.WinUI;
#endif

namespace LVA.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Logging.AddDebug();

        // ── Core services (DI composition root, TZ §16.1) ──────────────
        var options = LoadOptions();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(IntentTaxonomy.Default);

        // History store (SQLite + FTS5).
        var historyPath = Path.Combine(FileSystem.AppDataDirectory, "history.db");
        builder.Services.AddSingleton<IChatHistoryStore>(_ => new SqliteChatHistoryStore(historyPath));

        // Plugins: host + HTTP + secrets.
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<ISecretStore, SecureStorageSecretStore>();
        builder.Services.AddSingleton<IPluginSettingsStore, PluginSettingsStore>();
        builder.Services.AddSingleton(sp =>
        {
            var host = new PluginHost(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ISecretStore>());
            // Built-in WebSearch plugin (also loadable from plugins/ dir).
            var plugin = new LVA.Plugins.WebSearch.WebSearchPlugin();
            host.RegisterAsync(plugin, LoadPluginSettings()).GetAwaiter().GetResult();
            return host;
        });

        // Orchestration.
        builder.Services.AddSingleton<TaskTracker>();
        builder.Services.AddSingleton<NotificationHub>();
        builder.Services.AddSingleton<ToolRouter>();
        builder.Services.AddSingleton(sp => new Orchestrator(
            sp.GetRequiredService<PluginHost>(),
            sp.GetRequiredService<ToolRouter>(),
            sp.GetRequiredService<TaskTracker>(),
            sp.GetRequiredService<NotificationHub>(),
            options.Plugins));

        // Companion.
        builder.Services.AddSingleton<CompanionStateMachine>();

        // Window manager (platform-specific).
#if WINDOWS
        builder.Services.AddSingleton<IWindowManager, WindowsWindowManager>();
#endif

        // ViewModels + pages.
        builder.Services.AddSingleton<ConversationViewModel>();
        builder.Services.AddTransient<Views.MainPage>();
        builder.Services.AddTransient<Views.CompanionWindowPage>();
        builder.Services.AddTransient<Views.ApplicationSettingsPage>();

        return builder.Build();
    }

    private static LvaOptions LoadOptions()
    {
        // TODO: load appsettings.local.json from AppData when present.
        return new LvaOptions();
    }

    private static IReadOnlyDictionary<string, string> LoadPluginSettings()
    {
        // Plugin settings from configuration (TZ §6.3.3) — env/config file in a later pass.
        var settings = new Dictionary<string, string>();
        foreach (var key in new[] { "SearchProvider", "MaxPages", "Llm.Provider", "Llm.Endpoint", "Llm.Model", "FallbackToLocalSummary" })
        {
            var preferenceKey = PluginSettingsStore.Key("lva.websearch", key);
            if (Preferences.ContainsKey(preferenceKey))
                settings[key] = Preferences.Get(preferenceKey, string.Empty);

            var value = Environment.GetEnvironmentVariable($"LVA_WEBSEARCH_{key.Replace('.', '_').ToUpperInvariant()}");
            if (value is not null)
                settings[key] = value;
        }

        settings["SearchApiKeySecret"] = PluginSettingsStore.Key("lva.websearch", "SearchApiKey");
        settings["Llm.ApiKeySecret"] = PluginSettingsStore.Key("lva.websearch", "Llm.ApiKey");
        return settings;
    }
}

/// <summary>ISecretStore over MAUI SecureStorage (OS credential store).</summary>
public sealed class SecureStorageSecretStore : ISecretStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try { return await SecureStorage.GetAsync(key); }
        catch { return null; }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => SecureStorage.SetAsync(key, value);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        SecureStorage.Remove(key);
        return Task.CompletedTask;
    }
}
