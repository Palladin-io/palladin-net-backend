using JetBrains.Annotations;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Commands;

[PublicAPI]
public sealed record ResumeAgentDeprovisioningCommand(Guid OrganizationId, Guid OperationId) : IIntegrationCommand;
