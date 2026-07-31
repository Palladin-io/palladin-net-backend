namespace Palladin.Core.Security;

// Runtime switch for the email-verification gate. Implemented by the Identity module (backed by
// Modules:Identity:EmailVerification:GateEnabled) and resolved by RequireEmailVerifiedPreProcessor so
// the gate can be turned off via config without a redeploy (e.g. if email delivery is degraded).
public interface IEmailVerificationGate
{
    bool IsEnabled { get; }
}
