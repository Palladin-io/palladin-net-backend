using MassTransit;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Contracts.Commands;

namespace Palladin.Module.Identity.Infrastructure.Sharing;

internal sealed class EntrySharingRevocationOptions
{
    internal const string Position = "Modules:Identity:EntrySharingRevocation";
    public int RequestTimeoutSeconds { get; init; } = 10;
}

internal sealed class EntrySharingRevocation(
    IRequestClient<RevokeMemberEntrySharingCommand> client,
    IOptions<EntrySharingRevocationOptions> options)
{
    internal async Task RevokeMemberAsync(
        Guid organizationId, Guid userId, uint authorizationVersion, Instant now, CancellationToken ct)
    {
        try
        {
            var response = await client.GetResponse<MemberEntrySharingRevoked>(
                new RevokeMemberEntrySharingCommand(organizationId, userId, authorizationVersion, now),
                ct, RequestTimeout.After(s: options.Value.RequestTimeoutSeconds));
            if (response.Message.OrganizationId != organizationId || response.Message.UserId != userId
                || response.Message.AuthorizationVersion < authorizationVersion)
            {
                throw new EntrySharingRevocationUnavailableException();
            }
        }
        catch (RequestTimeoutException)
        {
            throw new EntrySharingRevocationUnavailableException();
        }
        catch (RequestFaultException)
        {
            throw new EntrySharingRevocationUnavailableException();
        }
    }
}

internal sealed class EntrySharingRevocationUnavailableException()
    : ConflictException("Sharing revocation could not be confirmed. Retry the operation.");
