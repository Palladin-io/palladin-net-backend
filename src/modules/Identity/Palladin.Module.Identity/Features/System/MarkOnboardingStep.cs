using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Identity.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Features;

[UsedImplicitly]
internal sealed class MarkOnboardingStepConsumerDefinition : ConsumerDefinition<MarkOnboardingStepConsumer>
{
    public MarkOnboardingStepConsumerDefinition() => EndpointName = IdentityEndpoints.Onboarding;
}

// Identity consumes its OWN onboarding command and materializes the per-user milestone. Monotonic:
// a re-delivered command for an already-set step no-ops.
[UsedImplicitly]
internal sealed class MarkOnboardingStepConsumer(
    IdentityDomainWriteContext domainWriteContext) : IConsumer<MarkOnboardingStepCommand>
{
    public async Task Consume(ConsumeContext<MarkOnboardingStepCommand> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var user = await domainWriteContext.Users.FirstOrDefaultAsync(u => u.Id == msg.UserId, ct);
        if (user is null)
        {
            return;
        }

        if (user.MarkOnboardingStep(msg.Step))
        {
            await domainWriteContext.CommitAsync(ct);
        }
    }
}
