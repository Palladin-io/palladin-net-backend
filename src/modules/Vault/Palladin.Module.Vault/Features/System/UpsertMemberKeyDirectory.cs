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
        var fingerprint = MemberKeyFingerprint.Compute(msg.RawPublicKey);
        var version = new MemberRecipientKeyVersion(msg.KeyVersion);
        var existing = await domainWriteContext.MemberKeyDirectory
            .SingleOrDefaultAsync(x => x.UserId == msg.UserId, context.CancellationToken);

        if (existing is null)
        {
            domainWriteContext.Add(MemberKeyDirectoryEntry.Create(
                msg.UserId,
                version,
                fingerprint,
                msg.RawPublicKey,
                msg.UpdatedAt));
        }
        else
        {
            existing.Replace(version, fingerprint, msg.RawPublicKey, msg.UpdatedAt);
        }

        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
