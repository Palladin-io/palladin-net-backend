using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Identity.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Features;

[UsedImplicitly]
internal sealed class MarkOrganizationOnboardingStepConsumerDefinition
    : ConsumerDefinition<MarkOrganizationOnboardingStepConsumer>
{
    public MarkOrganizationOnboardingStepConsumerDefinition() => EndpointName = IdentityEndpoints.Onboarding;
}

// Identity consumes its OWN org-onboarding command and materializes the org-level milestone. Monotonic:
// a re-delivered command for an already-set step no-ops.
[UsedImplicitly]
internal sealed class MarkOrganizationOnboardingStepConsumer(
    IdentityDomainWriteContext domainWriteContext) : IConsumer<MarkOrganizationOnboardingStepCommand>
{
    public async Task Consume(ConsumeContext<MarkOrganizationOnboardingStepCommand> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var organization = await domainWriteContext.Organizations.FirstOrDefaultAsync(o => o.Id == msg.OrganizationId, ct);
        if (organization is null)
        {
            return;
        }

        if (organization.MarkOnboardingStep(msg.Step))
        {
            await domainWriteContext.CommitAsync(ct);
        }
    }
}
