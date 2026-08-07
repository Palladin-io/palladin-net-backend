using Palladin.Core.Security;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.Login;

[UsedImplicitly]
internal sealed class EmailVerificationGate(IOptions<EmailVerificationOptions> options) : IEmailVerificationGate
{
    public bool IsEnabled => options.Value.GateEnabled;
}
