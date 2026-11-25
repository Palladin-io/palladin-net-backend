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
}

[PublicAPI]
public sealed record LoginSaltResponse(
    Guid AccountId,
    string ProfileId,
    ushort SecurityVersion,
    string KdfSalt,
    int MemoryKiB,
    int Iterations,
    int Parallelism,
    bool AccountSecretRequired);

[UsedImplicitly]
internal sealed class LoginSaltValidator : Validator<LoginSaltRequest>
{
    public LoginSaltValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
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
        // authenticate. Unknown emails receive deterministic pseudo AccountId and salt values, so
        // the response shape never reveals whether an account exists.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Fetch the login auth salt";
            summary.Description = "Returns the account's registered KDF profile and public client-side bootstrap. "
                + "Clients must run only the returned registered profile and never fall back to an older one. "
                + "Unknown or non-password accounts get stable pseudo-random AccountId and salt values under the "
                + "current profile.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(LoginSaltRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var bootstrap = await loginSaltService.GetBootstrapAsync(email, ct);
        await Send.OkAsync(new LoginSaltResponse(
            bootstrap.AccountId,
            bootstrap.Profile.Id,
            bootstrap.Profile.SecurityVersion,
            WebEncoders.Base64UrlEncode(bootstrap.KdfSalt),
            bootstrap.Profile.MemoryKiB,
            bootstrap.Profile.Iterations,
            bootstrap.Profile.Parallelism,
            bootstrap.Profile.AccountSecretRequired), ct);
    }
}
