using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyAssistant.ApprovalPolicy;

namespace MuseLite;

/// <summary>
/// Outbound email, gated for approval, sent via an app-only (client-credentials) Microsoft Graph
/// <c>sendMail</c> call. The app registration and mailbox come from the <c>Email</c> configuration
/// section (<c>TenantId</c>, <c>ClientId</c>, <c>ClientSecret</c>, <c>SenderAddress</c>); the real
/// values live in gitignored dev settings. All mail is sent FROM <c>SenderAddress</c>;
/// <see cref="SendSelfEmailAsync"/> also sends TO it, so it can only ever reach the owner.
/// <para>
/// Every send (success or failure) is appended to an <c>email-send.log</c> in the policy store (the
/// same agent-inaccessible store as the approval rules and audit log). This matters because
/// <c>send_self_email</c> is auto-approved by a code predicate that does not write the approval audit
/// log — so the tool itself provides the accountability trail for auto-sent mail.
/// </para>
/// </summary>
internal sealed class EmailTools
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    // One shared, app-lifetime HttpClient (the documented pattern — avoids socket exhaustion). Static so
    // the instance holds no disposable member of its own; never disposed because it lives until process exit.
    private static HttpClient SharedHttpClient { get; } = new();

    private string? TenantId { get; }

    private string? ClientId { get; }

    private string? ClientSecret { get; }

    private string? SenderAddress { get; }

    private IPolicyTextStore PolicyStore { get; }

    private string SendLogName { get; }

    private bool IsConfigured { get; }

    // Cached app-only token. Not lock-guarded: this is a single-user assistant with AllowMultipleToolCalls
    // off, so there is no real concurrency on the tools; the worst a race could cause is a redundant token
    // fetch, and both tokens would be valid. A 120-second safety margin avoids using a near-expired token.
    private string? CachedToken { get; set; }

    private DateTimeOffset TokenExpiresAt { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailTools"/> class.
    /// </summary>
    /// <param name="tenantId">The Entra tenant id (<c>Email:TenantId</c>).</param>
    /// <param name="clientId">The app registration's client id (<c>Email:ClientId</c>).</param>
    /// <param name="clientSecret">The app registration's client secret (<c>Email:ClientSecret</c>).</param>
    /// <param name="senderAddress">The mailbox mail is sent from, and the owner's own address (<c>Email:SenderAddress</c>).</param>
    /// <param name="policyStore">The policy store the append-only send log is written to.</param>
    /// <param name="sendLogName">The send-log document name within the policy store (e.g. <c>email-send.log</c>).</param>
    public EmailTools(string? tenantId, string? clientId, string? clientSecret, string? senderAddress, IPolicyTextStore policyStore, string sendLogName)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(sendLogName);
        this.TenantId = tenantId;
        this.ClientId = clientId;
        this.ClientSecret = clientSecret;
        this.SenderAddress = senderAddress;
        this.PolicyStore = policyStore;
        this.SendLogName = sendLogName;
        this.IsConfigured =
            IsConfiguredValue(tenantId) &&
            IsConfiguredValue(clientId) &&
            IsConfiguredValue(clientSecret) &&
            IsConfiguredValue(senderAddress);
    }

    /// <summary>
    /// Sends an email to an arbitrary recipient. The approval gate must have fired before this runs.
    /// </summary>
    /// <param name="to">The recipient email address.</param>
    /// <param name="subject">The email subject line.</param>
    /// <param name="body">The plain-text email body.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A short confirmation (or failure) string the assistant can relay to the user.</returns>
    public async Task<string> SendEmailAsync(
        [Description("The recipient email address.")] string to,
        [Description("The email subject line.")] string subject,
        [Description("The plain-text email body.")] string body,
        CancellationToken cancellationToken)
    {
        if (this.IsConfigured is false)
        {
            return NotConfiguredMessage();
        }

        try
        {
            (bool ok, string detail) = await this.SendViaGraphAsync(to, subject, body, cancellationToken).ConfigureAwait(false);
            await this.AppendSendLogAsync("send_email", to, subject, body.Length, ok, detail, cancellationToken).ConfigureAwait(false);
            return ok is true
                ? $"Email sent to {to} (subject: '{subject}')."
                : $"Email was NOT sent to {to}: {detail}";
        }
        catch (HttpRequestException ex)
        {
            await this.AppendSendLogAsync("send_email", to, subject, body.Length, false, ex.Message, cancellationToken).ConfigureAwait(false);
            return $"Email was NOT sent to {to}: network error ({ex.Message}).";
        }
    }

    /// <summary>
    /// Sends a message to the owner (the user) themselves — a summary, reminder, or notification. The
    /// recipient is fixed to the configured owner mailbox, so the model cannot direct it to anyone else.
    /// </summary>
    /// <param name="subject">The email subject line.</param>
    /// <param name="body">The plain-text email body.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A short confirmation (or failure) string the assistant can relay to the user.</returns>
    public async Task<string> SendSelfEmailAsync(
        [Description("The email subject line.")] string subject,
        [Description("The plain-text email body.")] string body,
        CancellationToken cancellationToken)
    {
        if (this.IsConfigured is false)
        {
            return NotConfiguredMessage();
        }

        // Recipient is the configured owner mailbox, fixed here and never supplied by the model.
        string to = this.SenderAddress!;

        try
        {
            (bool ok, string detail) = await this.SendViaGraphAsync(to, subject, body, cancellationToken).ConfigureAwait(false);
            await this.AppendSendLogAsync("send_self_email", to, subject, body.Length, ok, detail, cancellationToken).ConfigureAwait(false);
            return ok is true
                ? $"Email sent to you (subject: '{subject}')."
                : $"Email was NOT sent to you: {detail}";
        }
        catch (HttpRequestException ex)
        {
            await this.AppendSendLogAsync("send_self_email", to, subject, body.Length, false, ex.Message, cancellationToken).ConfigureAwait(false);
            return $"Email was NOT sent to you: network error ({ex.Message}).";
        }
    }

    /// <summary>
    /// Acquires (or returns a cached) app-only access token for Microsoft Graph.
    /// </summary>
    private async Task<(bool Ok, string? Token, string Detail)> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        string? cached = this.CachedToken;
        if (cached is not null && DateTimeOffset.UtcNow < this.TokenExpiresAt)
        {
            return (true, cached, "cached");
        }

        var tokenUrl = new Uri($"https://login.microsoftonline.com/{this.TenantId}/oauth2/v2.0/token");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = this.ClientId!,
            ["client_secret"] = this.ClientSecret!,
            ["scope"] = GraphScope,
            ["grant_type"] = "client_credentials",
        });

        using HttpResponseMessage response = await SharedHttpClient.PostAsync(tokenUrl, form, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode is false)
        {
            return (false, null, $"token request failed (HTTP {(int)response.StatusCode}): {Truncate(json)}");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("access_token", out JsonElement tokenElement) is false ||
            tokenElement.GetString() is not string accessToken)
        {
            return (false, null, "token response did not contain an access_token");
        }

        int expiresIn = root.TryGetProperty("expires_in", out JsonElement expiresElement) is true
            ? expiresElement.GetInt32()
            : 3600;

        this.CachedToken = accessToken;
        this.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 120);
        return (true, accessToken, "ok");
    }

    /// <summary>
    /// Posts a single plain-text message to the Graph <c>sendMail</c> endpoint for the configured mailbox.
    /// </summary>
    private async Task<(bool Ok, string Detail)> SendViaGraphAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        (bool tokenOk, string? token, string tokenDetail) = await this.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (tokenOk is false || token is null)
        {
            return (false, tokenDetail);
        }

        // Build the request body with JsonObject so subject/body are properly JSON-escaped (they are
        // user/model text and must never be string-concatenated into JSON).
        var payload = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = subject,
                ["body"] = new JsonObject
                {
                    ["contentType"] = "Text",
                    ["content"] = body,
                },
                ["toRecipients"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["emailAddress"] = new JsonObject { ["address"] = to },
                    },
                },
            },
            ["saveToSentItems"] = true,
        };

        var sendUrl = new Uri($"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(this.SenderAddress!)}/sendMail");
        using var request = new HttpRequestMessage(HttpMethod.Post, sendUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode is true)
        {
            // sendMail returns 202 Accepted with an empty body on success.
            return (true, $"HTTP {(int)response.StatusCode}");
        }

        string error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (false, $"Graph sendMail returned HTTP {(int)response.StatusCode}: {Truncate(error)}");
    }

    /// <summary>
    /// Appends one tab-separated line per send attempt to the durable, inspectable send log. The body
    /// itself is never written (only its length) to avoid logging message contents.
    /// </summary>
    private async Task AppendSendLogAsync(string tool, string to, string subject, int bodyLength, bool ok, string detail, CancellationToken cancellationToken)
    {
        string status = ok is true ? "SENT" : "FAILED";
        string line =
            $"{DateTimeOffset.UtcNow:o}\t{status}\t{tool}\tto={Sanitize(to)}\tsubject={Sanitize(subject)}\tbodyLen={bodyLength}\t{Sanitize(detail)}";

        await this.PolicyStore.AppendLineAsync(this.SendLogName, line, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A configuration value counts as "set" only if it is non-blank and is not the committed placeholder
    /// sentinel ("see dev version of app settings file").
    /// </summary>
    private static bool IsConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) is false &&
        value.Contains("see dev version", StringComparison.OrdinalIgnoreCase) is false;

    private static string NotConfiguredMessage() =>
        "Email was not sent: the Email settings (TenantId / ClientId / ClientSecret / SenderAddress) are "
        + "not configured. Set them in the dev app settings and retry.";

    private static string Sanitize(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string Truncate(string value, int max = 500) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), " (truncated)");
}
