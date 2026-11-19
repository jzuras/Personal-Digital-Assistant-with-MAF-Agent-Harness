using MuseLite.UI.Components;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AGUI.Client;

namespace MuseLite.UI;

/// <summary>
/// Entry point for the Agentic UI AG-UI Blazor client.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // When the client targets the Entra-protected hosted agent, every request to it needs a bearer
        // token. The local target is anonymous, so this is off by default and the fast local loop is
        // unaffected. Flip it on via config (AgentAuthMode=AzureAD) alongside the hosted AgentBaseUrl /
        // AgentEndpoint / AgentEndpointQuery (the hosted invocations path needs "?api-version=v1").
        var useAzureAuth = string.Equals(
            builder.Configuration["AgentAuthMode"], "AzureAD", StringComparison.OrdinalIgnoreCase);

        if (useAzureAuth is true)
        {
            var scope = builder.Configuration["AgentAuthScope"] ?? "https://ai.azure.com/.default";
            builder.Services.AddSingleton(new AgentTokenProvider(scope));
            builder.Services.AddTransient<AgentBearerTokenHandler>();
        }

        var agentHttpClient = builder.Services.AddHttpClient("agent", client =>
        {
            var url = builder.Configuration["AgentBaseUrl"] ?? "http://localhost:5152";
            client.BaseAddress = new Uri(url);
        });

        if (useAzureAuth is true)
        {
            agentHttpClient.AddHttpMessageHandler<AgentBearerTokenHandler>();
        }

        builder.Services.AddSingleton<AIAgent>(sp =>
        {
            var endpoint = builder.Configuration["AgentEndpoint"] ?? "invocations";
            // The hosted invocations path carries "?api-version=v1"; locally this is empty. Kept as a separate
            // config value so it rides the endpoint without being baked into AgentEndpoint. Both chat and the
            // custom state operations (history / reset / approvals) use this same route on the "agent" client:
            // the ops ride /invocations with an x-client-op header (the gateway-legal multiplex), so they
            // inherit the bearer auth and the "?api-version=v1" query in every environment.
            var query = builder.Configuration["AgentEndpointQuery"] ?? string.Empty;
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("agent");
            var chatClient = new AGUIChatClient(new(httpClient, endpoint + query));
            return chatClient.AsAIAgent(name: "Agentic Generative UI");
        });

        var app = builder.Build();

        if (app.Environment.IsDevelopment() is false)
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
    }
}
