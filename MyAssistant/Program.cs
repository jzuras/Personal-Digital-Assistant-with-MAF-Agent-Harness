using Azure.AI.Projects;
using Azure.Identity;
using Harness.Shared.Console;
using Harness.Shared.Console.Observers;
using Harness.Shared.Console.OpenAI;
using Harness.Shared.Console.ToolFormatters;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using MyAssistant.ApprovalPolicy;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace MyAssistant;

#pragma warning disable OPENAI001 // Suppress experimental API warnings for Responses API usage.
#pragma warning disable MAAI001 // Suppress experimental API warnings for FileAccessStore.

internal class Program
{
    static async Task Main(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                   ?? "Development";

        // below required adding Microsoft.Extensions.Configuration.Json package to the project.
        var configuration = new ConfigurationBuilder()
            //.SetBasePath(AppContext.BaseDirectory) // un-nec if file is in application's working directory
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables() // grabs normal env vars
            .Build();

        var endpoint = configuration["FOUNDRY_PROJECT_ENDPOINT"] ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");
        var deploymentName = configuration["FOUNDRY_MODEL"] ?? "gpt-5.4";

        // note - i changed these instructions to NOT say memory file for watchlist and to use the file_access tools
        // <instructions>
        var instructions =
            """
    ## Personal Finance Assistant Instructions

    You are a personal finance and investing assistant. You help the user understand their
    watchlist and the markets. When asked about a stock, look up its current price with the
    get_stock_price tool, and use web search for recent news, earnings, or analyst commentary.

    ### Working style

    - Always verify numbers with a tool rather than relying on memory. Stock prices change.
    - Cite web sources inline when you use them.
    - Keep the user's watchlist in a file called watchlist.md using the file_access tools: read it when reviewing the
      watchlist, and update it whenever the user adds or removes a ticker.

    ### Important

    You provide information and analysis only — you are not a licensed financial advisor and you
    must not present your output as personalized investment advice. Remind the user to do their
    own research before making decisions.
    """;
        // </instructions>


        #region Azure OpenAI Responses API Client Setup
        // <create_client>
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
            .GetResponsesClient()
            .AsIChatClient(deploymentName);
        // </create_client>
        #endregion


        // Phase 1 — durable approval policy. The working/ folder holds both the file-access
        // store and the durable approval artifacts (approval-rules.json + approval-audit.log),
        // so standing permissions survive across sessions. The policy component is a standalone
        // library (MyAssistant.ApprovalPolicy) with no console/Blazor deps, so it ports to the
        // server unchanged.
        string workingDir = Path.Combine(AppContext.BaseDirectory, "working");

        // Durable, inspectable, agent-curated memory in a STABLE location. The MAF harness's
        // default FileMemoryProvider scopes memory to a per-session {timestamp}_{guid} folder —
        // even under a custom store root — so pointing a store at a fixed folder alone does NOT
        // give durability (verified: it just relocates the per-session folders). Instead we disable
        // the built-in provider (DisableFileMemory) and add our own whose stateInitializer. This PINS the
        // working folder to a constant, so every session reads/writes the same files. Kept under a
        // dedicated memory/ dir (separate from working/, which file access owns) to avoid
        // same-filename collisions.
        string memoryDir = Path.Combine(AppContext.BaseDirectory, "memory");
        const string durableMemoryFolder = "durable";
        var fileMemoryProvider = new FileMemoryProvider(
            new FileSystemAgentFileStore(memoryDir),
            stateInitializer: _ => new FileMemoryState { WorkingFolder = durableMemoryFolder });
        var approvalAuditLog = new FileApprovalAuditLog(Path.Combine(workingDir, "approval-audit.log"));
        var approvalStore = new ApprovalRuleStore(Path.Combine(workingDir, "approval-rules.json"), approvalAuditLog);
        // The hard floor: tools whose approvals may never be remembered. Empty for now — no
        // money/credential tool exists yet (get_stock_price is a pure read gated only for testing).
        var humanOnlyPolicy = new HumanOnlyPolicy();

        // <create_agent>
        // Turn the chat client into a HarnessAgent. AsHarnessAgent pre-configures function invocation,
        // per-service-call chat history persistence, TodoProvider, AgentModeProvider, and web search.
        // We add finance instructions and our get_stock_price tool.
        AIAgent agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            // I added this from Part 2 to test having files get written outside of the session dir
            FileAccessStore = new FileSystemAgentFileStore(workingDir),

            // Replace the built-in per-session FileMemoryProvider with our folder-pinned one (above).
            DisableFileMemory = true,
            AIContextProviders = [fileMemoryProvider],

            // Auto-approval predicates, evaluated before any prompt is surfaced. Read-only file
            // tools flow freely (reads flow, writes gate). Our durable predicate then auto-approves
            // any exact call the user has remembered permanently — including a new session's first
            // call, which is what makes grants cross sessions. A human-only tool is never
            // auto-approved here even if a rule somehow exists (defense in depth against hand edits).
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                // Safety cap on auto-approval-driven re-invocations within a single run. This is
                // the ONLY bound on that loop (MaximumIterationsPerRequest restarts each
                // re-invocation and cannot bound it); without it, a model that keeps requesting an
                // auto-approved tool drives unbounded billable calls. On hitting the cap the agent
                // surfaces the next request to the human instead of auto-approving. Matters more
                // for us because durable standing grants auto-approve silently across sessions.
                // Pinned to the v1.24.0 framework default (40) so a future package update can't
                // silently change our safety posture; 40 comfortably covers a heavy legit turn
                // (multi-ticker watchlist review). Lower it for a tighter posture.
                MaxAutoApprovalIterations = 40,
                AutoApprovalRules =
                [
                    FileAccessProvider.ReadOnlyToolsAutoApprovalRule,
                    async ctx =>
                    {
                        FunctionCallContent call = ctx.FunctionCallContent;
                        if (!humanOnlyPolicy.IsPersistable(call.Name))
                        {
                            return false;
                        }

                        string canonicalArgs = ArgCanonicalizer.Canonicalize(call.Arguments);
                        return await approvalStore.IsApprovedAsync(call.Name, canonicalArgs).ConfigureAwait(false);
                    },
                ],
            },

            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = [ClawSample.StockTools.CreateGetStockPriceTool()],
                Reasoning = new() { Effort = ReasoningEffort.Medium },
            },
        });
        // </create_agent>

        // The durable approval observer surfaces the prompt and, on an explicit "remember
        // permanently" confirmation, writes the grant to the same store the predicate reads.
        var toolFormatters = ToolCallFormatter.BuildDefaultToolFormatters();
        var approvalObserver = new ToolApprovalObserver(toolFormatters, approvalStore, humanOnlyPolicy);

        // <run>
        // Run the interactive console session using the shared HarnessConsole helper.
        await HarnessConsole.RunAgentAsync(
            agent,
            userPrompt: "Ask about a stock or say 'Review my watchlist and recommend some stocks to add' to get started.",
            new HarnessConsoleOptions
            {
                Observers = [
                    new OpenAIResponsesWebSearchDisplayObserver(),
                    new OpenAIResponsesErrorObserver(),
                    .. HarnessConsoleOptions.BuildObserversWithPlanning(
                        agent,
                        planModeName: "plan",
                        executionModeName: "execute",
                        toolFormatters: toolFormatters,
                        approvalObserver: approvalObserver)],
                        CommandHandlers = HarnessConsoleOptions.BuildDefaultCommandHandlers(agent),
            });
        // </run>
    }
}
