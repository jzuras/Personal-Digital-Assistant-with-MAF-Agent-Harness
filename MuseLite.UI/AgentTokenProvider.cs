using Azure.Core;
using Azure.Identity;

namespace MuseLite.UI;

/// <summary>
/// Acquires and caches an Entra access token used to call the Entra-protected hosted agent, refreshing
/// it shortly before expiry. A single instance is shared for the application lifetime (registered as a
/// singleton) so both the token and its underlying credential are reused across requests. Only created
/// when the client targets the hosted agent; the anonymous local target never resolves it.
/// </summary>
internal sealed class AgentTokenProvider
{
    /// <summary>
    /// Credential used to acquire tokens. <see cref="DefaultAzureCredential"/> picks up whatever identity
    /// the host runs as (locally: the Azure CLI / Visual Studio login; in Azure: the managed identity).
    /// </summary>
    private TokenCredential Credential { get; }

    /// <summary>The scope the token is requested for (e.g. <c>https://ai.azure.com/.default</c>).</summary>
    private string[] Scopes { get; }

    /// <summary>Serializes refreshes so concurrent requests don't each call the token service.</summary>
    private SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

    /// <summary>The most recently acquired token; its default value has a null <c>Token</c> until first use.</summary>
    private AccessToken Cached { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentTokenProvider"/> class.
    /// </summary>
    /// <param name="scope">The token scope to request.</param>
    public AgentTokenProvider(string scope)
    {
        this.Credential = new DefaultAzureCredential();
        this.Scopes = [scope];
    }

    /// <summary>
    /// Returns a valid bearer token, refreshing it when it is missing or within two minutes of expiry.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the acquisition.</param>
    /// <returns>The bearer token string.</returns>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (this.IsFresh() is false)
        {
            await this.Gate.WaitAsync(cancellationToken);
            try
            {
                // Double-check inside the gate: another request may have refreshed while we waited.
                if (this.IsFresh() is false)
                {
                    this.Cached = await this.Credential.GetTokenAsync(
                        new TokenRequestContext(this.Scopes), cancellationToken);
                }
            }
            finally
            {
                this.Gate.Release();
            }
        }

        return this.Cached.Token;
    }

    /// <summary>
    /// Whether the cached token exists and is not within two minutes of expiring.
    /// </summary>
    /// <returns><see langword="true"/> if the cached token is still usable; otherwise <see langword="false"/>.</returns>
    private bool IsFresh()
    {
        return this.Cached.Token is not null
            && this.Cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2);
    }
}
