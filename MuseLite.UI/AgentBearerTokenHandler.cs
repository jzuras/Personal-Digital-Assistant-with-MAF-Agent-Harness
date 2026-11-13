using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;

namespace MuseLite.UI;

/// <summary>
/// A delegating handler that attaches an <c>Authorization: Bearer</c> header carrying an Entra token to
/// every outgoing request. Registered on the agent <see cref="HttpClient"/> only when the client targets
/// the Entra-protected hosted agent; the anonymous local target does not register it, so local dev keeps
/// working with no token. Because a Blazor Server client renders server-side, the token is acquired and
/// held on the server and never reaches the browser.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by the DI container via AddTransient / AddHttpMessageHandler.")]
internal sealed class AgentBearerTokenHandler : DelegatingHandler
{
    /// <summary>Supplies (and refreshes) the bearer token.</summary>
    private AgentTokenProvider TokenProvider { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentBearerTokenHandler"/> class.
    /// </summary>
    /// <param name="tokenProvider">The token provider.</param>
    public AgentBearerTokenHandler(AgentTokenProvider tokenProvider)
    {
        this.TokenProvider = tokenProvider;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string token = await this.TokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
