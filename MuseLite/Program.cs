using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MuseLite;

#pragma warning disable OPENAI001 // Suppress experimental API warnings for Responses API usage.
#pragma warning disable MAAI001 // Suppress experimental API warnings for FileAccessStore.

internal static class Program
{

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Load configuration from Azure App Configuration (it overrides local appsettings.json because the
        // other providers are added first by CreateBuilder()). Two ways to connect:
        //   - a connection string (local dev; its presence/absence is the on/off switch for App Config), or
        //   - an endpoint URI authenticated with the running identity via DefaultAzureCredential (keyless:
        //     a managed/agent identity when hosted, your dev identity when running locally). The identity
        //     must hold the "App Configuration Data Reader" data-plane role on the store.
        // If neither is set, App Configuration is skipped and only local appsettings.json is used.
        var connectionString = builder.Configuration.GetConnectionString("AppConfig");
        var endpoint = builder.Configuration["AppConfig:Endpoint"];
        // Startup timing: the ConfigurationManager loads the App Configuration source eagerly inside this
        // block, so the stopwatch captures the real network/auth cost. Logged after Build() (first point a
        // logger exists) to surface the App Configuration startup cost.
        var appConfigStopwatch = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(connectionString) is false || string.IsNullOrEmpty(endpoint) is false)
        {
            builder.Configuration.AddAzureAppConfiguration(options =>
            {
                if (string.IsNullOrEmpty(connectionString) is false)
                {
                    options.Connect(connectionString);
                }
                else if (string.IsNullOrEmpty(endpoint) is false)
                {
                    options.Connect(new Uri(endpoint), new DefaultAzureCredential());
                }

                // Load all keys that start with "MuseLite:"
                options.Select("MuseLite:*")
                       // Configure to reload configuration if the registered sentinel key is modified.
                       .ConfigureRefresh(refreshOptions =>
                            refreshOptions.Register("MuseLite:Settings:Sentinel", refreshAll: true));

                options.UseFeatureFlags(featureFlagOptions =>
                {
                    featureFlagOptions.Select("MuseLite:*");
                });
            });
        }

        appConfigStopwatch.Stop();


        // AgenticUI from dojo
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Add(AGUIDojoServerSerializerContext.Default));


        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.TypeInfoResolverChain.Add(AGUIDojoServerSerializerContext.Default);

        jsonOptions.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        var server = ServerComposition.Create(builder, jsonOptions);


        builder.Services.AddHttpClient().AddLogging();

        string[] allowedOrigins = builder.Configuration.GetSection("MuseLite:Cors:AllowedOrigins").Get<string[]>() ?? [];
        builder.Services.AddCors(options =>
            options.AddDefaultPolicy(policy =>
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()));

        builder.Services.AddAGUIServer();

        var app = builder.Build();

        // Map the agent to OpenAI-compatible /responses endpoints
        app.MapOpenAIResponses(server.Agent);

        // Startup diagnostic for comparing App Configuration load cost across the two apps. CA1848
        // (LoggerMessage delegates) and CA1873 (expensive-arg evaluation) are over-engineering for a
        // one-shot startup line whose argument is a trivial property read.
#pragma warning disable CA1848, CA1873
        app.Logger.LogInformation(
            "Azure App Configuration startup block took {ElapsedMs} ms.",
            appConfigStopwatch.ElapsedMilliseconds);
#pragma warning restore CA1848, CA1873

        app.UseCors();

        // Warm-up short-circuit — answered here, BEFORE the AG-UI handler, so it never reaches the model or
        // the durable session. A hosted Foundry agent exposes no client-reachable readiness endpoint (the
        // platform /readiness probe is not proxied to callers), so the client probes availability by POSTing a
        // no-op "warm-up" invocation carrying this header. The x-client-* namespace is guaranteed forwarded to
        // the container, and the inbound request alone wakes a scaled-to-zero micro-VM — so a 204 both answers
        // "reachable" and warms the agent for the user's first real turn. Uniform local and hosted.
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (string.Equals(context.Request.Headers["x-client-warmup"], "true", StringComparison.OrdinalIgnoreCase) is true)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        // Client state-operation dispatch — after the warm-up short-circuit, BEFORE the payload adapter. The
        // three custom durable operations (history / reset / approvals) ride the one gateway-legal route by
        // dispatching on an x-client-op header before any AG-UI or platform-payload parsing. The protocol-only
        // hosted gateway refuses sibling routes like GET /history, but forwards the x-client-* namespace, so
        // the client POSTs /invocations with this header instead. MUST precede the payload adapter below: that
        // adapter rejects (400) any JSON object body lacking messages/input/message, which is exactly the
        // approvals op body. A recognized op writes its response and stops here; anything else continues.
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (await InvocationEndpoints.TryDispatchClientOpAsync(context, server, jsonOptions).ConfigureAwait(false) is true)
            {
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        // Platform-native payload adaptation — after the warm-up short-circuit, BEFORE the AG-UI handler.
        // Foundry's own invokers (Portal Playground, Routines) deliver their prompt to /invocations in
        // non-AG-UI shapes (text/plain raw prompt; {"input": …} JSON), which the AG-UI endpoint would
        // otherwise 415 or run as an empty turn. The adapter rewrites those bodies into a RunAgentInput
        // whose threadId is a reserved transient session key, so the durable session store keeps such
        // runs away from the user's one durable context. Real AG-UI bodies pass through untouched.
        ILogger adapterLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(FoundryInvocationAdapter));
        var routineLauncher = new RoutineRunLauncher(
            app.Services.GetRequiredService<IHttpClientFactory>(),
            app.Services.GetRequiredService<IHostApplicationLifetime>(),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(RoutineRunLauncher)),
            () => ResolveLoopbackBaseAddress(app.Urls));
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            switch (await FoundryInvocationAdapter.TryAdaptAsync(context, adapterLogger).ConfigureAwait(false))
            {
                case FoundryInvocationAdapter.AdaptationOutcome.Rejected:
                    return;
                case FoundryInvocationAdapter.AdaptationOutcome.AdaptedForPlayground:
                    // The playground renders only the raw-text invocations wire format, so the
                    // AG-UI SSE output is buffered and rewritten as plain text for this face.
                    await FoundryInvocationAdapter.RunWithPlainTextResponseAsync(context, next).ConfigureAwait(false);
                    return;
                case FoundryInvocationAdapter.AdaptationOutcome.AdaptedForRoutine:
                {
                    // Fire-and-accept: the Routine dispatcher enforces a 30-second per-attempt
                    // timeout and needs only a 2xx acceptance — dispatch is a handshake, the work
                    // is expected to be asynchronous. The turn runs in the background (over
                    // loopback, through this same pipeline); a dispatch that arrives while a run
                    // is in flight (the dispatcher retries identical payloads) is acknowledged
                    // without starting a duplicate run.
                    byte[] routineRun;
                    using (var bodyBuffer = new MemoryStream())
                    {
                        await context.Request.Body.CopyToAsync(bodyBuffer, context.RequestAborted).ConfigureAwait(false);
                        routineRun = bodyBuffer.ToArray();
                    }

                    bool started = routineLauncher.TryLaunch(routineRun);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        started is true
                            ? """{"accepted":true}"""
                            : """{"accepted":false,"reason":"a routine run is already in progress"}""",
                        context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                default:
                    await next(context).ConfigureAwait(false);
                    return;
            }
        });

        // Explicit UseRouting, AFTER the adapter. WebApplication otherwise inserts routing at the very
        // START of the pipeline, so endpoint selection — including the content-type check that picks the
        // framework's "415 Unsupported Media Type" endpoint — would run against the ORIGINAL request and
        // pre-select the 415 endpoint before the adapter ever rewrites a text/plain body (probe-verified:
        // without this line the playground face still 415s even though the adapter ran). Placing routing
        // here makes endpoint selection see the rewritten request.
        app.UseRouting();

        // Single AG-UI mount. An Azure AI Foundry Hosted Agent forwards each invocation body to /invocations
        // and streams the AG-UI SSE response back; the local client targets the same route directly. The
        // three custom durable operations (history / reset / approvals) are NOT sibling routes — the
        // protocol-only gateway refuses those — but ride this same route via the x-client-op dispatch
        // middleware above (before routing), so one endpoint value drives both chat and the custom calls in
        // every environment (the query string the hosted path needs is a separate client-side config value).
        app.MapAGUIServer("/invocations", server.Agent);

        // Platform readiness probe (GET /readiness). The custom durable operations are served by the
        // x-client-op dispatch middleware above, not by mapped routes. See InvocationEndpoints.
        app.MapReadinessProbe();

        await app.RunAsync();
    }

    /// <summary>
    /// Derives the server's own loopback base address from its listen addresses, for the routine
    /// launcher's self-call. Listen addresses use wildcard hosts (<c>0.0.0.0</c>, <c>[::]</c>,
    /// <c>+</c>) that are not directly connectable, so only the port is kept and the host becomes
    /// <c>localhost</c>. Addresses are only known after startup, which is why this is resolved at
    /// launch time rather than at composition time.
    /// </summary>
    /// <param name="urls">The server's listen addresses (<c>app.Urls</c>).</param>
    /// <returns>A connectable <c>http://localhost:port</c> base address.</returns>
    private static string ResolveLoopbackBaseAddress(ICollection<string> urls)
    {
        string? address = urls.FirstOrDefault(static url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? urls.FirstOrDefault();
        if (address is null)
        {
            // The Dockerfile pins the hosted container to this port; a bare local run defaults here too.
            return "http://localhost:8088";
        }

        string connectable = address
            .Replace("0.0.0.0", "localhost", StringComparison.Ordinal)
            .Replace("[::]", "localhost", StringComparison.Ordinal)
            .Replace("+", "localhost", StringComparison.Ordinal)
            .Replace("*", "localhost", StringComparison.Ordinal);
        var uri = new Uri(connectable);
        return $"http://localhost:{uri.Port}";
    }

}
