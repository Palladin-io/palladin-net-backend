using FastEndpoints;
using JetBrains.Annotations;
using Palladin.Core.Security;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class DeleteEntryEndpoint(EntryLifecycleService lifecycleService)
    : ChangeEntryStateEndpointBase(lifecycleService)
{
    public override void Configure() => ConfigureMemberMutation(
        "api/vaults/{vaultId:guid}/entries/{entryId:guid}/delete",
        "Move an encrypted Entry to Recently Deleted",
        "Creates a recoverable Deleted revision, removes Agent Discovery and destroys secret-bearing grant material.");

    public override async Task HandleAsync(ChangeEntryStateRequest req, CancellationToken ct)
    {
        if (req.AgentDiscovery is not null)
        {
            AddError("Recently Deleted cannot publish Agent Discovery ciphertext.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await SendResultAsync(await LifecycleService.DeleteAsync(
            User.GetOrganizationId()!.Value,
            User.GetUserId()!.Value,
            req,
            ct), ct);
    }
}
