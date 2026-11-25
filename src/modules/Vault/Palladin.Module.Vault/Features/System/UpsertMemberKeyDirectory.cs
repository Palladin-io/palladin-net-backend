using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class UpsertMemberKeyDirectoryConsumerDefinition
    : ConsumerDefinition<UpsertMemberKeyDirectoryConsumer>
{
    public UpsertMemberKeyDirectoryConsumerDefinition() => EndpointName = VaultEndpoints.MemberKeyDirectory;
}

[UsedImplicitly]
internal sealed class UpsertMemberKeyDirectoryConsumer(VaultDomainWriteContext domainWriteContext)
    : IConsumer<UpsertMemberKeyDirectoryCommand>
{
    public async Task Consume(ConsumeContext<UpsertMemberKeyDirectoryCommand> context)
    {
        var msg = context.Message;
        var fingerprint = RecipientKeyFingerprint.Compute(msg.PublicKeySuiteId, VaultKeyKind.MemberEncryption, msg.RawPublicKey);
        var version = new MemberRecipientKeyVersion(msg.KeyVersion);
        var existing = await domainWriteContext.MemberKeyDirectory
            .SingleOrDefaultAsync(x => x.UserId == msg.UserId, context.CancellationToken);

        if (existing is null)
        {
            if (await domainWriteContext.DiscardedUsers.AnyAsync(discarded => discarded.UserId == msg.UserId, context.CancellationToken))
            {
                return;
            }

            domainWriteContext.Add(MemberKeyDirectoryEntry.Create(
                msg.UserId,
                version,
                msg.PublicKeySuiteId,
                fingerprint,
                msg.RawPublicKey,
                msg.UpdatedAt));
        }
        else
        {
            existing.Replace(version, msg.PublicKeySuiteId, fingerprint, msg.RawPublicKey, msg.UpdatedAt);
        }

        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
