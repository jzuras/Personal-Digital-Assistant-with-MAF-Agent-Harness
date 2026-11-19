using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace MuseLite;

#pragma warning disable OPENAI001 // Responses API (ResponsesClient) is experimental.
#pragma warning disable MAAI001 // AsIChatClientWithStoredOutputDisabled (store=false) is an experimental harness API.

/// <summary>
/// Builds the assistant's chat client, selecting the provider from configuration
/// (<c>MuseLite:UseOpenRouter</c>): the free OpenRouter Responses endpoint, or the Azure AI Foundry
/// project (the default). Both run client-managed history.
/// </summary>
internal static class ChatClientFactory
{
    /// <summary>
    /// Creates the provider-selected chat client.
    /// </summary>
    /// <param name="builder">The web application builder, for configuration access.</param>
    /// <returns>The configured <see cref="IChatClient"/>.</returns>
    public static IChatClient Create(WebApplicationBuilder builder)
    {
        // Provider selector — single source of truth, config-driven via MuseLite:UseOpenRouter.
        // Locally this reads from appsettings; when running as a hosted agent it comes from Azure App
        // Configuration (the same MuseLite:* source as the OpenRouter key), so the model can be switched
        // without a redeploy. Absent key defaults to false (the Azure/Foundry model).
        // ⚠ The durable session snapshot in context/ is PROVIDER-SPECIFIC: an OpenRouter-written
        // snapshot loaded into Azure (or vice-versa) causes an Azure HTTP 400 invalid_payload. Clear
        // the context snapshot when switching providers.
        bool useOpenRouter = builder.Configuration.GetValue<bool>("MuseLite:UseOpenRouter");
        return useOpenRouter is true
            ? CreateOpenRouterChatClient(builder)
            : CreateAzureOpenAIChatClient(builder);
    }

    private static IChatClient CreateOpenRouterChatClient(WebApplicationBuilder builder)
    {
        var credential = builder.Configuration["MuseLite:OPENROUTER_API_KEY"];

        if (credential is null)
        {
            throw new InvalidOperationException("Unable to find OPENROUTER_API_KEY.");
        }

        var openAIOptions = new ResponsesClientOptions()
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1")
        };

        string model = "nvidia/nemotron-3.5-lightning:free";

        IChatClient chatClient = new ResponsesClient(new ApiKeyCredential(credential), openAIOptions).AsIChatClient(model);
        return chatClient;
    }

    private static IChatClient CreateAzureOpenAIChatClient(WebApplicationBuilder builder)
    {
        // FOUNDRY_PROJECT_ENDPOINT stays a plain env var: the Foundry Hosted Agents platform auto-injects it,
        // so it is NOT namespaced under MuseLite:* / App Configuration. FOUNDRY_MODEL is ordinary app config.
        var endpoint = builder.Configuration["FOUNDRY_PROJECT_ENDPOINT"] ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");
        var deploymentName = builder.Configuration["MuseLite:FOUNDRY_MODEL"] ?? "gpt-5.4";

        // Construct an IChatClient. Here we use a Microsoft Foundry project: the endpoint points at the
        // project, DefaultAzureCredential handles auth, and the deployment name selects the model.
        // The harness works with ANY IChatClient — see the AgentProviders samples for OpenAI, Azure
        // OpenAI, Anthropic, Google Gemini, Ollama, ONNX, and more.
        IChatClient chatClient =
            new AIProjectClient(
                new Uri(endpoint),
                // WARNING: DefaultAzureCredential is convenient for development but requires careful consideration in production.
                // In production, consider using a specific credential (e.g., ManagedIdentityCredential) to avoid
                // latency issues, unintended credential probing, and potential security risks from fallback mechanisms.
                new DefaultAzureCredential(),
                new AIProjectClientOptions { RetryPolicy = new ClientRetryPolicy(3) })  // Enable retries to improve resiliency.
            .GetProjectOpenAIClient()
            // store=false (client-managed history). Agent Framework keeps the FULL transcript locally in the
            // session (InMemoryChatHistoryProvider) and sends it each turn, instead of letting Azure hold
            // history server-side behind a previous_response_id / conversationId pointer (the default,
            // store=true). This is required for the "one durable context that's OURS" model: it makes
            // context/session.json self-contained (does not depend on Azure's ~30-day server-side retention)
            // and ELIMINATES the stale-pointer (previous_response_not_found) failure class entirely — so no
            // server-side SessionRecoveringAgent is needed on this path. Token/context-window cost is unchanged (the whole chain is billed
            // as input either way per the Responses API); only request bandwidth grows (gzip-able). The small
            // free OpenRouter Responses endpoint is already client-managed, so this aligns the two providers.
            // includeReasoningEncryptedContent:true carries Azure's encrypted reasoning items in local history
            // so reasoning continuity is preserved under store=false.
            .GetProjectResponsesClient()
            .AsIChatClientWithStoredOutputDisabled(deploymentName, includeReasoningEncryptedContent: true);

        return chatClient;
    }
}
