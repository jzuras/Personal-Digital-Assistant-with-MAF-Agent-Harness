using Azure.Identity;
using MyAssistant.ApprovalPolicy;
using MyAssistant.DurableContext;
using System.Diagnostics;
using System.Text.Json;

namespace MuseLite.Companion;

/// <summary>
/// The MuseLite companion: a small sidecar API for the durable-state operations the chat protocol does
/// not cover — history hydration, new-conversation reset, and persisting a standing approval. The hosted
/// Foundry agent gateway proxies only the declared protocol endpoints, so these operations cannot ride
/// custom routes on the hosted agent; instead this separate app reads and writes the SAME durable stores
/// (session snapshot + policy documents) directly, composed via the shared <see cref="DurableStorage"/>
/// helper so the two processes can never drift on where that state lives. Chat itself never flows
/// through here.
/// </summary>
internal static class Program
{
    /// <summary>Web-cased serializer options for the endpoint payloads (matches the agent server's shapes).</summary>
    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Load configuration from Azure App Configuration — the same dual connect path and MuseLite:*
        // namespace as the agent server, so both processes read identical durable-storage settings from
        // the one shared store. A connection string (local dev; its presence is the on/off switch) or an
        // endpoint URI with DefaultAzureCredential (keyless: managed identity hosted, dev identity
        // locally; needs "App Configuration Data Reader"). Neither set → local appsettings.json only.
        var connectionString = builder.Configuration.GetConnectionString("AppConfig");
        var endpoint = builder.Configuration["AppConfig:Endpoint"];
        // Startup timing: the ConfigurationManager loads the App Configuration source eagerly inside this
        // block, so the stopwatch captures the real network/auth cost. Logged after Build() (first point a
        // logger exists) to compare startup cost against the agent server's identical block.
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

                // Load all keys that start with "MuseLite:", refreshing on the shared sentinel.
                options.Select("MuseLite:*")
                       .ConfigureRefresh(refreshOptions =>
                            refreshOptions.Register("MuseLite:Settings:Sentinel", refreshAll: true));
            });
        }

        appConfigStopwatch.Stop();

        // Durable storage — the SAME composition the agent server uses (one shared helper: backend flag,
        // Blob client, container/directory selection), so both processes resolve the identical stores. One
        // companion-only wrinkle: in File mode each process roots at its OWN base directory, which would be
        // this app's bin folder — a different context/ than the server writes. Blob mode has no such issue
        // (same account + containers) and is this app's real operating mode; for local File-mode testing,
        // point DurableContext:File:BaseDirectory at the server's base directory instead.
        string? baseOverride = builder.Configuration["DurableContext:File:BaseDirectory"];
        string baseDirectory = string.IsNullOrWhiteSpace(baseOverride) is true
            ? AppContext.BaseDirectory
            : baseOverride;
        DurableStorage storage = DurableStorage.Create(builder.Configuration, baseDirectory);

        // History read path: the shared no-model reader (deserializes the stored snapshot with a dummy
        // client and projects it to {role, text, images}) — no agent, no model credentials, in this process.
        var historyReader = new DurableHistoryReader(storage.SnapshotStore);

        // Approval policy write path. The rule store is the same document the agent's auto-approval
        // predicate reads (that predicate loads it fresh on every gated call, so a grant written here is
        // honored by the running agent immediately). No audit log is wired here: the audit log records
        // auto-approvals at decision time, which only the agent process does — the written rule itself
        // (with grantedUtc) is the record of the grant.
        var approvalStore = new ApprovalRuleStore(storage.PolicyStore, "approval-rules.json");

        // The human-only floor for the persist-approval re-check below, from the single shared source
        // (HumanOnlyPolicy.CreateDefault()) the agent server also uses — so a tool floored there is always
        // floored here too, and a raw POST to this app can never persist a grant the agent side refuses.
        var humanOnlyPolicy = HumanOnlyPolicy.CreateDefault();

        var app = builder.Build();

        // Startup diagnostic for comparing App Configuration load cost across the two apps. CA1848
        // (LoggerMessage delegates) and CA1873 (expensive-arg evaluation) are over-engineering for a
        // one-shot startup line whose argument is a trivial property read.
#pragma warning disable CA1848, CA1873
        app.Logger.LogInformation(
            "Azure App Configuration startup block took {ElapsedMs} ms.",
            appConfigStopwatch.ElapsedMilliseconds);
#pragma warning restore CA1848, CA1873

        // Server-authoritative history hydration (same payload as the agent server's local endpoint): a
        // freshly opened client GETs this on load to paint the whole conversation instead of starting blank.
        // An empty list means no durable context yet.
        app.MapGet("/invocations/history", async (CancellationToken cancellationToken) =>
        {
            IReadOnlyList<HistoryMessage> history = await historyReader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(history, JsonOptions);
        });

        // Memory-preserving "new conversation" reset. Archives the one durable context (timestamped) and
        // leaves no current snapshot, so the agent's next turn starts an empty conversation; the
        // user-editable working/ store and standing approval rules are separate and untouched. The agent
        // server loads the session from the store on every request, so the archive takes effect on its very
        // next turn with no restart. This runs in a separate process from the agent's own snapshot writes —
        // UI-side coordination keeps the two apart, with a storage-level precondition as the planned backstop.
        app.MapPost("/invocations/reset", async (CancellationToken cancellationToken) =>
        {
            try
            {
                await storage.SnapshotStore.ArchiveAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok();
            }
            catch (SnapshotConcurrencyException)
            {
                // A turn was written to the session concurrently (the agent server rewrites it every turn
                // in a separate process), so the archive conditional-delete failed and the reset did not
                // take effect. 409 tells the client the reset lost a race and can be retried; the newer
                // snapshot is still current, so the conversation is intact.
                return Results.Conflict();
            }
        });

        // Durable "remember this approval permanently" write path for the client's approval UI. This
        // endpoint ONLY persists the rule — approving the current call is a separate action the client does
        // over the chat protocol. Two safeguards, mirroring the agent server's local endpoint: (1) the
        // human-only floor is re-checked here so a raw POST can never persist a grant for a tool that must
        // always be a fresh human decision; (2) the arguments are canonicalized server-side with the same
        // ArgCanonicalizer the agent's auto-approval predicate uses, so the stored rule is guaranteed to match.
        app.MapPost("/invocations/approvals", async (PersistApprovalRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.ToolName) is true)
            {
                return Results.BadRequest();
            }

            if (humanOnlyPolicy.IsPersistable(request.ToolName) is false)
            {
                return Results.Json(new PersistApprovalResult(false, "human-only"), JsonOptions);
            }

            // Arguments arrive as the raw JSON object the client received in the approval request.
            // Deserialize to the same Dictionary<string, object?> shape the predicate canonicalizes (values
            // become JsonElement, exactly as FunctionCallContent.Arguments does), so the canonical forms
            // match. A tool with no arguments canonicalizes to "{}".
            Dictionary<string, object?>? arguments = request.Arguments.ValueKind == JsonValueKind.Object
                ? request.Arguments.Deserialize<Dictionary<string, object?>>(JsonOptions)
                : null;
            string canonicalArgs = ArgCanonicalizer.Canonicalize(arguments);
            await approvalStore.AddRuleAsync(request.ToolName, canonicalArgs).ConfigureAwait(false);
            return Results.Json(new PersistApprovalResult(true, null), JsonOptions);
        });

        // Reachability probe: the client checks this to decide whether history/reset/"remember" are
        // available (chat health is independent — companion-down degrades those features, never chat).
        app.MapGet("/readiness", () => Results.Ok(new { status = "ready" }));

        await app.RunAsync().ConfigureAwait(false);
    }

    // Body of the "remember this grant permanently" request from the client's approval UI. Carries the
    // gated tool's name and the exact arguments it was called with; this app canonicalizes the arguments
    // (so the written rule matches the agent's auto-approval predicate, which canonicalizes the same way)
    // and writes a durable rule. Serialized camelCase ({toolName, arguments}).
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by ASP.NET Core minimal-API JSON body binding.")]
    private sealed record PersistApprovalRequest(string ToolName, JsonElement Arguments);

    // Result of a persist-approval request. Persisted is false (with a reason) when the grant could not be
    // stored — today only because the tool is on the human-only floor, which can never receive a durable
    // grant. The client treats a non-persisted result as "approve once" and tells the user it was not saved.
    private sealed record PersistApprovalResult(bool Persisted, string? Reason);
}
