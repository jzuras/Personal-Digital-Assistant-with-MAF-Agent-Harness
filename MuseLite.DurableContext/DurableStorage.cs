using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using MyAssistant.ApprovalPolicy;

namespace MyAssistant.DurableContext;

/// <summary>
/// Composes the three durable stores — session snapshot, policy/accountability, and agent working
/// memory — from configuration, selected by the <c>DurableContext:Backend</c> flag: <c>File</c>
/// (the default) keeps the fast local loop on the filesystem under <c>baseDirectory</c>;
/// <c>Blob</c> routes all three to Azure Blob over one shared <see cref="BlobServiceClient"/>
/// (separate containers). Shared by every process that touches the durable state — the chat
/// server and the companion app — so the backend selection, the configuration-key layout, and
/// the container/directory names cannot drift between them.
/// </summary>
/// <remarks>
/// <para>
/// The policy store is deliberately SEPARATE from the agent's working store: it holds the
/// approval-rule document and the append-only audit/send logs, which the agent must be able to
/// neither read nor alter (its <c>file_access</c> tools reach only the working store). A local
/// <c>policy/</c> directory in dev, its own blob container in the cloud — never the working
/// container.
/// </para>
/// <para>
/// Blob credentials follow the same split the rest of the app uses:
/// <c>DurableContext:Blob:ConnectionString</c> is the LOCAL (Azurite) path and stays a plain
/// local key, while <c>MuseLite:DurableContext:Blob:AccountUri</c> is environment-invariant and
/// rides in Azure App Configuration; when only the account URI is present the client
/// authenticates keyless via <see cref="DefaultAzureCredential"/> (a managed/agent identity when
/// hosted, the dev identity locally), which must hold "Storage Blob Data Contributor" on the
/// account.
/// </para>
/// </remarks>
public sealed class DurableStorage
{
    private const string DefaultWorkingContainer = "assistant-working";
    private const string DefaultContextContainer = "assistant-context";
    private const string DefaultPolicyContainer = "assistant-policy";

    /// <summary>
    /// Gets a value indicating whether the stores are blob-backed (<c>DurableContext:Backend</c>
    /// is <c>Blob</c>) rather than filesystem-backed.
    /// </summary>
    public bool UseBlob { get; }

    /// <summary>
    /// Gets the session-snapshot store (the one durable context). File mode roots it at
    /// <c>context/</c> under the base directory; blob mode uses the context container.
    /// </summary>
    public ISnapshotStore SnapshotStore { get; }

    /// <summary>
    /// Gets the policy/accountability store (approval rules + append-only audit and send logs),
    /// kept agent-inaccessible. File mode roots it at <c>policy/</c> under the base directory;
    /// blob mode uses the policy container.
    /// </summary>
    public IPolicyTextStore PolicyStore { get; }

    /// <summary>
    /// Gets the agent's working-memory file store (the user-editable <c>working/</c> documents
    /// reached by the <c>file_access</c> tools). File mode roots it at <c>working/</c> under the
    /// base directory; blob mode uses the working container.
    /// </summary>
    public AgentFileStore WorkingStore { get; }

    private DurableStorage(bool useBlob, ISnapshotStore snapshotStore, IPolicyTextStore policyStore, AgentFileStore workingStore)
    {
        this.UseBlob = useBlob;
        this.SnapshotStore = snapshotStore;
        this.PolicyStore = policyStore;
        this.WorkingStore = workingStore;
    }

    /// <summary>
    /// Composes the durable stores from configuration. Construction does no I/O — directories and
    /// containers are created lazily on first write by the stores themselves.
    /// </summary>
    /// <param name="configuration">The app configuration carrying the <c>DurableContext:Backend</c>
    /// flag and the <c>MuseLite:DurableContext:Blob:*</c> settings.</param>
    /// <param name="baseDirectory">The root for the file-mode <c>working/</c>, <c>context/</c> and
    /// <c>policy/</c> directories (typically <see cref="AppContext.BaseDirectory"/>).</param>
    /// <returns>The composed stores.</returns>
    public static DurableStorage Create(IConfiguration configuration, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        bool useBlob = string.Equals(configuration["DurableContext:Backend"], "Blob", StringComparison.OrdinalIgnoreCase) is true;

        if (useBlob is false)
        {
            return new DurableStorage(
                useBlob: false,
                new FileSnapshotStore(Path.Combine(baseDirectory, "context")),
                new FilePolicyTextStore(Path.Combine(baseDirectory, "policy")),
                new FileSystemAgentFileStore(Path.Combine(baseDirectory, "working")));
        }

        BlobServiceClient blobService = CreateBlobServiceClient(configuration);
        string workingContainer = configuration["MuseLite:DurableContext:Blob:WorkingContainer"] ?? DefaultWorkingContainer;
        string contextContainer = configuration["MuseLite:DurableContext:Blob:ContextContainer"] ?? DefaultContextContainer;
        string policyContainer = configuration["MuseLite:DurableContext:Blob:PolicyContainer"] ?? DefaultPolicyContainer;

        return new DurableStorage(
            useBlob: true,
            new BlobSnapshotStore(blobService.GetBlobContainerClient(contextContainer)),
            new BlobPolicyTextStore(blobService.GetBlobContainerClient(policyContainer)),
            new BlobAgentFileStore(blobService.GetBlobContainerClient(workingContainer)));
    }

    // Builds the one BlobServiceClient shared by all three blob containers: a connection string (the
    // local Azurite emulator) if present, otherwise an account URI authenticated keyless via
    // DefaultAzureCredential (see the class remarks for the key split and the required RBAC role).
    private static BlobServiceClient CreateBlobServiceClient(IConfiguration configuration)
    {
        string? connectionString = configuration["DurableContext:Blob:ConnectionString"];
        string? accountUri = configuration["MuseLite:DurableContext:Blob:AccountUri"];

        if (string.IsNullOrWhiteSpace(connectionString) is false)
        {
            return new BlobServiceClient(connectionString);
        }

        if (string.IsNullOrWhiteSpace(accountUri) is false)
        {
            return new BlobServiceClient(new Uri(accountUri), new DefaultAzureCredential());
        }

        throw new InvalidOperationException(
            "DurableContext:Backend is 'Blob' but neither DurableContext:Blob:ConnectionString nor " +
            "MuseLite:DurableContext:Blob:AccountUri is set.");
    }
}
