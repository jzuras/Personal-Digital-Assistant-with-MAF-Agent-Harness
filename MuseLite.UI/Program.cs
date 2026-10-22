using MuseLite.UI.Components;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AGUI.Client;

namespace MuseLite.UI;

/// <summary>
/// Entry point for the Agentic UI AG-UI Blazor client.
/// </summary>
public sealed class Program
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

        builder.Services.AddHttpClient("agent", client =>
        {
            var url = builder.Configuration["AgentBaseUrl"] ?? "http://localhost:5152";
            client.BaseAddress = new Uri(url);
        });

        builder.Services.AddSingleton<AIAgent>(sp =>
        {
            var endpoint = builder.Configuration["AgentEndpoint"] ?? "agentic_generative_ui";
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("agent");
            var chatClient = new AGUIChatClient(new(httpClient, endpoint));
            return chatClient.AsAIAgent(name: "Agentic Generative UI");
        });

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
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
