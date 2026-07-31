using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Domain;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.WebUtilities;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record LoginSaltRequest
{
    public string Email { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record LoginSaltResponse(
    Guid? AccountId,
    string ProfileId,
    ushort SecurityVersion,
    string KdfSalt,
    int MemoryKiB,
    int Iterations,
    int Parallelism);

[UsedImplicitly]
internal sealed class LoginSaltValidator : Validator<LoginSaltRequest>
{
    public LoginSaltValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.ProfileId).Equal(IdentityKdfProfiles.CurrentProfileId);
    }
}

[PublicAPI]
internal sealed class LoginSaltEndpoint(ILoginSaltService loginSaltService)
    : Endpoint<LoginSaltRequest, LoginSaltResponse>
{
    public override void Configure()
    {
        Post("api/auth/login/salt");
        // Anonymous by design: the client needs the auth salt to derive its authHash before it can
        // authenticate. Unknown emails receive a deterministic pseudo-salt, so this never reveals
        // whether an account exists.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Fetch the login auth salt";
            summary.Description = "Returns the client-side auth salt for an email. Unknown or non-password "
                + "accounts get a stable pseudo-random salt (HMAC of the email) so registration status leaks nothing.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(LoginSaltRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var bootstrap = await loginSaltService.GetBootstrapAsync(email, req.ProfileId, ct);
        await Send.OkAsync(new LoginSaltResponse(
            bootstrap.AccountId,
            IdentityKdfProfiles.CurrentProfileId,
            IdentityKdfProfiles.CurrentSecurityVersion,
            WebEncoders.Base64UrlEncode(bootstrap.KdfSalt),
            IdentityKdfProfiles.MemoryKiB,
            IdentityKdfProfiles.Iterations,
            IdentityKdfProfiles.Parallelism), ct);
    }
}
