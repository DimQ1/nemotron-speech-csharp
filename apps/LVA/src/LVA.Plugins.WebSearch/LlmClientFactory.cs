using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LVA.Plugins.WebSearch;

/// <summary>LLM provider configuration (TZ §6.3.3).</summary>
public sealed record LlmSettings(
    string Provider,          // AzureOpenAI | OpenAI | OpenAICompatible | GitHubModels
    string Endpoint,
    string Model,
    string ApiKeySecret,      // key name in ISecretStore
    int MaxTokens = 2000,
    float Temperature = 0.2f);

/// <summary>
/// Builds a provider-independent IChatClient from plugin settings (TZ §6.3.3).
/// The core never knows the provider — it's entirely the plugin's zone.
/// </summary>
public static class LlmClientFactory
{
    public static IChatClient Create(LlmSettings settings, string apiKey)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(settings.Endpoint) &&
            settings.Provider is "OpenAICompatible" or "AzureOpenAI" or "GitHubModels")
        {
            options.Endpoint = new Uri(settings.Endpoint);
        }

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        return client.GetChatClient(settings.Model).AsIChatClient();
    }
}
