using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Recovery;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record IssueRecoveryChallengeResponse(
    ushort Version,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[] SealedChallenge,
    Instant ExpiresAt);

[PublicAPI]
internal sealed class IssueRecoveryChallengeEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IOptions<AccountRecoveryOptions> options,
    IClock clock) : EndpointWithoutRequest<IssueRecoveryChallengeResponse>
{
    public override void Configure()
    {
        Post("api/account/recovery/challenge");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Issue an account recovery challenge";
            summary.Description = "Seals a fresh single-use secret to the account's current Member key: version 2 "
                + "(X-Wing, 1168 bytes) for current-suite keys, version 1 (sealed box) for accounts that have not "
                + "upgraded yet. Only a client that unlocked the private key (for example with the recovery phrase) "
                + "can open it and present the secret as recoveryProof to PUT api/account/recovery. A new challenge "
                + "replaces any outstanding one.";
        });
        Tags("Identity/Account");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is not { IsOnboarded: true, PublicKey: { } publicKey, MemberPublicKeySuiteId: { } suiteId })
        {
            await Send.NotFoundAsync(ct);
            return;
        }


        var secret = RandomNumberGenerator.GetBytes(RecoveryChallengeSealer.SecretBytes);
        try
        {
            var expiresAt = clock.GetCurrentInstant() + Duration.FromSeconds(options.Value.ChallengeTtlSeconds);
            var (version, sealedChallenge) = RecoveryChallenges.Seal(user.Id, secret, publicKey, suiteId);
            user.IssueRecoveryChallenge(secret, expiresAt);
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(new IssueRecoveryChallengeResponse(version, sealedChallenge, expiresAt), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
