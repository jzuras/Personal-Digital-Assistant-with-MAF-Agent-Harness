using System.Text.Json;
using MuseLite.UI.Models;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;

namespace MuseLite.UI.Components.Pages.Chat;

/// <summary>
/// Code-behind for the main chat page. Handles streaming responses from the AG-UI
/// agent and extracts predictive state snapshots and JSON patch deltas.
/// </summary>
public partial class Chat : IAsyncDisposable
{
    [Inject]
    private ILogger<Chat> Logger { get; set; } = default!;

    [Inject]
    private AIAgent Agent { get; set; } = default!;

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private AgentSession? activeSession;
    private readonly List<ChatMessage> messages = [];
    private ChatInput? chatInput;
    private int statefulMessageCount;
    private string sessionId = Guid.Empty.ToString();
    private ChatMessage? currentResponseMessage;
    private CancellationTokenSource? currentResponseCancellation;
    private AgentState? agentState;

    // Holds a pending approval request surfaced by a gated server tool. When non-null, the UI
    // shows the tool + its arguments with Approve/Deny; the decision is sent back on the same
    // session. Showing the arguments matters because approval can be keyed on the exact tool+args
    // combination (e.g. record_note with a specific message).
    private ToolApprovalRequestContent? pendingApproval;

    /// <summary>
    /// AdditionalProperties key set on the follow-up assistant message when a tool approval is
    /// denied, so <c>ChatMessageItem</c> can render a subtle inline "denied" marker. Client-only —
    /// it is display state, never sent to the agent as content.
    /// </summary>
    internal const string ToolDeniedPropertyKey = "tool_denied";

    private string? PendingApprovalToolName => (this.pendingApproval?.ToolCall as FunctionCallContent)?.Name;

    /// <summary>
    /// The pending tool call's arguments as display-ready key/value pairs, for the approval prompt.
    /// Empty when there is no pending request or the tool takes no arguments.
    /// </summary>
    private IReadOnlyList<KeyValuePair<string, string>> PendingApprovalArgs =>
        this.pendingApproval?.ToolCall is FunctionCallContent { Arguments: { Count: > 0 } args }
            ? [.. args.Select(kv => new KeyValuePair<string, string>(kv.Key, FormatArgValue(kv.Value)))]
            : [];

    /// <summary>
    /// Renders a tool-argument value for display/logging. Over the wire, argument values arrive as
    /// <see cref="JsonElement"/>; strings are unwrapped, everything else shown as raw JSON.
    /// </summary>
    private static string FormatArgValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? string.Empty,
        JsonElement je => je.GetRawText(),
        _ => value.ToString() ?? string.Empty,
    };

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        this.activeSession = await this.Agent.CreateSessionAsync();
        this.sessionId = Guid.NewGuid().ToString();
        this.Logger.LogInformation("Started session {SessionId}", this.sessionId);
    }

    private async Task AddUserMessageAsync(ChatMessage userMessage)
    {
        this.CancelAnyCurrentResponse();
        this.pendingApproval = null;

        this.messages.Add(userMessage);
        if (this.chatInput is not null)
        {
            await this.chatInput.FocusAsync();
        }

        var responseText = new TextContent("");
        this.currentResponseMessage = new ChatMessage(ChatRole.Assistant, [responseText]);
        this.currentResponseCancellation = new CancellationTokenSource();

        var outboundMessages = this.BuildOutboundMessages();
        await this.GetStreamingResponseAsync(outboundMessages, responseText, this.currentResponseMessage,
            this.currentResponseCancellation.Token);

        this.messages.Add(this.currentResponseMessage!);
        this.statefulMessageCount = this.activeSession is not null ? this.messages.Count : 0;
        this.currentResponseMessage = null;
    }

    private async Task SendSuggestionAsync(string text)
    {
        await this.AddUserMessageAsync(new ChatMessage(ChatRole.User, text));
    }

    /// <summary>
    /// Resume path (documented MAF/AG-UI model): answers a pending
    /// <see cref="ToolApprovalRequestContent"/> by sending a follow-up turn on the SAME
    /// <see cref="AgentSession"/> carrying ONLY the user's decision. We do NOT re-send the request:
    /// stateless resume (request+response in the message list) is rejected by ChatClientAgent since
    /// MAF PR #8641. The server must hold the pending request via session persistence; AGUIChatClient
    /// encodes the response as RunAgentInput.Resume[] so the server resumes the paused run.
    /// </summary>
    private async Task RespondToApprovalAsync(bool approved)
    {
        var request = this.pendingApproval;
        if (request is null)
        {
            return;
        }

        this.pendingApproval = null;

        var responseText = new TextContent("");
        this.currentResponseMessage = new ChatMessage(ChatRole.Assistant, [responseText]);
        this.currentResponseCancellation = new CancellationTokenSource();

        if (!approved)
        {
            // Subtle inline marker so the user sees the gate acted. The denied tool's result is a
            // plain "rejected" string the thread would not otherwise surface, so tag the follow-up
            // assistant message; ChatMessageItem renders a muted "denied" line above its text.
            var deniedTool = (request.ToolCall as FunctionCallContent)?.Name ?? "the tool";
            this.currentResponseMessage.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ToolDeniedPropertyKey] = deniedTool,
            };
        }

        List<ChatMessage> resumeTurn =
        [
            new(ChatRole.User, [request.CreateResponse(approved, approved ? "Approved by user." : "Denied by user.")]),
        ];

        this.Logger.LogInformation(
            "Resuming approval (response-only): reqId={ReqId} approved={Approved}",
            request.RequestId,
            approved);

        await this.GetStreamingResponseAsync(
            resumeTurn,
            responseText,
            this.currentResponseMessage,
            this.currentResponseCancellation.Token);

        this.messages.Add(this.currentResponseMessage!);
        this.statefulMessageCount = this.activeSession is not null ? this.messages.Count : 0;
        this.currentResponseMessage = null;
    }

    private async Task GetStreamingResponseAsync(
        IEnumerable<ChatMessage> outboundMessages,
        TextContent responseText,
        ChatMessage responseMessage,
        CancellationToken cancellationToken)
    {
        this.Logger.LogInformation("Streaming started");
        var updateCount = 0;

        try
        {
            await foreach (var update in this.Agent.RunStreamingAsync(
                outboundMessages,
                this.activeSession,
                cancellationToken: cancellationToken))
            {
                if (update is null)
                {
                    continue;
                }

                updateCount++;
                this.Logger.LogDebug(
                    "Update #{Count}: role={Role} contents=[{Types}] props=[{Props}] text={Text}",
                    updateCount,
                    update.Role,
                    string.Join(", ", update.Contents.Select(c => c is DataContent dc
                        ? $"Data({dc.MediaType},{dc.Data.Length}b)"
                        : c.GetType().Name)),
                    string.Join(", ", update.AdditionalProperties?.Keys ?? []),
                    update.Text is { Length: > 0 } t ? $"\"{t[..Math.Min(t.Length, 40)]}\"" : "-");

                foreach (var fc in update.Contents.OfType<FunctionCallContent>())
                {
                    this.Logger.LogInformation("  Tool call: {Name} ({CallId})", fc.Name, fc.CallId);
                }

                // A gated tool surfaces a ToolApprovalRequestContent instead of executing. Stash it
                // and show Approve/Deny with the tool's arguments; the turn ends here
                // (RUN_FINISHED outcome=interrupt).
                var approvalRequest = update.Contents.OfType<ToolApprovalRequestContent>().FirstOrDefault();
                if (approvalRequest is not null)
                {
                    this.pendingApproval = approvalRequest;
                    var fcc = approvalRequest.ToolCall as FunctionCallContent;
                    this.Logger.LogInformation(
                        "  >>> Approval request received: reqId={ReqId} tool={Tool} args={Args}",
                        approvalRequest.RequestId,
                        fcc?.Name,
                        fcc?.Arguments is { Count: > 0 } a
                            ? string.Join("; ", a.Select(kv => $"{kv.Key}={FormatArgValue(kv.Value)}"))
                            : "(none)");
                    this.StateHasChanged();
                    continue;
                }

                var stateFromToolResult = false;
                foreach (var fr in update.Contents.OfType<FunctionResultContent>())
                {
                    this.Logger.LogInformation("  Tool result ({CallId}): {Result}", fr.CallId, fr.Result);
                    stateFromToolResult |= this.TryExtractStateFromToolResult(fr);
                }

                if (stateFromToolResult)
                {
                    this.StateHasChanged();
                    continue;
                }

                // State snapshot: full replacement of agent state.
                // Primary signal: DataContent with application/json (confirmed by integration tests).
                // Fallback: AdditionalProperties flag set by AGUIChatClient.
                var isSnapshot = update.Contents.Any(c => c is DataContent { MediaType: "application/json" })
                    || (update.AdditionalProperties?.TryGetValue("is_state_snapshot", out var snapshotFlag) == true && snapshotFlag is true);
                if (isSnapshot)
                {
                    this.ProcessStateSnapshot(update);
                    this.StateHasChanged();
                    continue;
                }

                // State delta: JSON Patch applied to current agent state.
                // is_state_delta is untested in the NuGet package — use media type as primary signal.
                var isDelta = update.Contents.Any(c => c is DataContent { MediaType: "application/json-patch+json" })
                    || (update.AdditionalProperties?.TryGetValue("is_state_delta", out var deltaFlag) == true && deltaFlag is true);
                if (isDelta)
                {
                    this.ProcessStateDelta(update);
                    this.StateHasChanged();
                    continue;
                }

                // Error from agent
                var errorContent = update.Contents.OfType<ErrorContent>().FirstOrDefault();
                if (errorContent is not null)
                {
                    this.Logger.LogError(
                        "Agent returned error (code: {Code}): {Message}",
                        errorContent.ErrorCode,
                        errorContent.Message);
                    responseText.Text = $"Sorry, something went wrong: {errorContent.Message}";
                    ChatMessageItem.NotifyChanged(responseMessage);
                    this.StateHasChanged();
                    break;
                }

                // Regular streamed text
                if (update.Role == ChatRole.Assistant && update.Text is { Length: > 0 })
                {
                    responseText.Text += update.Text;
                    ChatMessageItem.NotifyChanged(responseMessage);
                }

                this.StateHasChanged();
                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            this.Logger.LogInformation("Streaming cancelled after {Count} updates", updateCount);
        }
        catch (JsonException ex)
        {
            this.Logger.LogError(
                ex,
                "JSON parsing error at position {Position}. The agent returned an unexpected format.",
                ex.BytePositionInLine);
            responseText.Text = "Error: The agent returned an unexpected response format.";
            ChatMessageItem.NotifyChanged(responseMessage);
            this.StateHasChanged();
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, "Unexpected streaming error after {Count} updates", updateCount);
            throw;
        }
        finally
        {
            this.Logger.LogInformation("Streaming exited after {Count} updates", updateCount);
        }
    }

    /// <summary>
    /// Extracts agent state from a tool result. AGUI.Client v0.0.4 does not emit
    /// DataContent for STATE_SNAPSHOT/STATE_DELTA — state arrives as tool results instead.
    /// Handles two formats: full AgentState JSON object (create_plan) and RFC 6902 patch
    /// array (update_plan_step).
    /// </summary>
    private bool TryExtractStateFromToolResult(FunctionResultContent fr)
    {
        var json = fr.Result switch
        {
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => fr.Result?.ToString()
        };

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        // A generic (non plan-state) tool result — e.g. record_note, or the deny sentinel
        // "Tool call invocation rejected." — is a plain string, not JSON. Only create_plan /
        // update_plan_step return a JSON object/array, so skip anything that is not JSON-shaped
        // rather than parsing it and catching a JsonReaderException on every generic tool.
        var jsonSpan = json.AsSpan().TrimStart();
        if (jsonSpan.IsEmpty || (jsonSpan[0] != '{' && jsonSpan[0] != '['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("steps", out _))
            {
                var state = JsonSerializer.Deserialize<AgentState>(json, s_jsonOptions);
                if (state is not null)
                {
                    this.agentState = state;
                    this.Logger.LogInformation(
                        "State snapshot from tool result ({CallId}): {Count} steps",
                        fr.CallId,
                        state.Steps.Count);
                    return true;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array && this.agentState is not null)
            {
                var patches = JsonSerializer.Deserialize<List<JsonPatchOperation>>(json, s_jsonOptions);
                if (patches is { Count: > 0 })
                {
                    this.ApplyJsonPatch(patches);
                    this.Logger.LogInformation(
                        "State delta from tool result ({CallId}): {Count} ops",
                        fr.CallId,
                        patches.Count);
                    return true;
                }
            }
        }
        catch (JsonException ex)
        {
            this.Logger.LogWarning(ex, "Could not parse tool result as state ({CallId})", fr.CallId);
        }

        return false;
    }

    private void ProcessStateSnapshot(AgentResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is DataContent dc && string.Equals(dc.MediaType, "application/json", StringComparison.Ordinal))
            {
                var state = JsonSerializer.Deserialize<AgentState>(dc.Data.Span, s_jsonOptions);
                if (state is not null)
                {
                    this.agentState = state;
                    this.Logger.LogInformation(
                        "State snapshot received: {Count} steps",
                        state.Steps.Count);
                }
            }
        }
    }

    private void ProcessStateDelta(AgentResponseUpdate update)
    {
        if (this.agentState is null)
        {
            return;
        }

        foreach (var content in update.Contents)
        {
            if (content is DataContent dc
                && string.Equals(dc.MediaType, "application/json-patch+json", StringComparison.Ordinal))
            {
                var patches = JsonSerializer.Deserialize<List<JsonPatchOperation>>(dc.Data.Span, s_jsonOptions);
                if (patches is not null)
                {
                    this.ApplyJsonPatch(patches);
                }
            }
        }
    }

    /// <summary>
    /// Applies RFC 6902 replace operations to the current agent state.
    /// Supports /steps/{index}/status and /steps/{index}/description paths.
    /// </summary>
    private void ApplyJsonPatch(List<JsonPatchOperation> operations)
    {
        foreach (var op in operations)
        {
            if (!string.Equals(op.Op, "replace", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = op.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3
                || !string.Equals(segments[0], "steps", StringComparison.Ordinal)
                || !int.TryParse(segments[1], out var index))
            {
                continue;
            }

            if (this.agentState is null
                || index < 0
                || index >= this.agentState.Steps.Count)
            {
                continue;
            }

            var step = this.agentState.Steps[index];

            if (string.Equals(segments[2], "status", StringComparison.Ordinal))
            {
                var statusStr = op.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? op.Value.GetString()
                    : null;
                step.Status = string.Equals(statusStr, "completed", StringComparison.OrdinalIgnoreCase)
                    ? StepStatus.Completed
                    : StepStatus.Pending;

                this.Logger.LogInformation(
                    "Step {Index} status → {Status}",
                    index,
                    step.Status);
            }
            else if (string.Equals(segments[2], "description", StringComparison.Ordinal))
            {
                step.Description = op.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? op.Value.GetString() ?? ""
                    : "";
            }
        }
    }

    private IEnumerable<ChatMessage> BuildOutboundMessages()
    {
        // Send only the messages added since the last stateful checkpoint
        return this.messages.Skip(this.statefulMessageCount);
    }

    private void CancelAnyCurrentResponse()
    {
        if (this.currentResponseMessage is not null)
        {
            this.messages.Add(this.currentResponseMessage);
        }

        this.currentResponseCancellation?.Cancel();
        this.currentResponseMessage = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.currentResponseCancellation is not null)
        {
            await this.currentResponseCancellation.CancelAsync();
            this.currentResponseCancellation.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
