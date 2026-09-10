using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using OtpNet;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Totp;
using Palladin.Module.Identity.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class SessionSecondFactorTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_LoginCompletesSecondFactor_Then_SessionRecordsActualConfiguration(bool recovery)
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var challenge = Guid.NewGuid().ToString("N");
        string code;
        uint revision;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var service = scope.ServiceProvider.GetRequiredService<ITotpService>();
            var secret = service.GenerateSecret();
            var factor = TotpCredential.StartEnrollment(user.Id, secret, now);
            factor.Confirm(0, now);
            revision = factor.ConfigurationRevision;
            db.TotpCredentials.Add(factor);
            db.VerificationTokens.Add(VerificationToken.CreateLoginChallenge(
                Guid.NewGuid(), user.Id, SecureToken.Hash(challenge), Duration.FromMinutes(5), now));
            if (recovery)
            {
                code = service.GenerateRecoveryCodes()[0];
                db.TotpRecoveryCodes.Add(TotpRecoveryCode.Create(
                    Guid.NewGuid(), user.Id, service.HashRecoveryCode(code)));
            }
            else
            {
                code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
            }
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // When
        var (response, result) = await apiFactory.CreateClient()
            .POSTAsync<LoginTotpEndpoint, LoginTotpRequest, AuthSessionResponse>(
                new LoginTotpRequest { ChallengeToken = challenge, Code = code });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var hash = TokenService.HashToken(result.RefreshToken);
        var session = await read.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.TokenHash == hash, TestContext.Current.CancellationToken);
        session.SecondFactorRevision.ShouldBe(revision);
        session.SecondFactorVerifiedAt.ShouldBe(now);
        var currentFactor = await read.TotpCredentials.SingleAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken);
        session.SatisfiesSecondFactor(currentFactor, now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task When_RefreshesSession_Then_PreservesAssuranceWithoutInventingOrRenewingIt(
        bool assured, bool rotate)
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var originalTime = now - Duration.FromDays(10);
        var rawToken = Guid.NewGuid().ToString("N");
        var original = RefreshToken.Create(Guid.NewGuid(), user.Id, organization.Id,
            TokenService.HashToken(rawToken), 1, now + Duration.FromDays(rotate ? 1 : 30), originalTime,
            assured ? 2u : null, assured ? originalTime : null);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            db.RefreshTokens.Add(original);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // When
        var (response, result) = await apiFactory.CreateClient()
            .POSTAsync<RefreshAccessTokenEndpoint, RefreshAccessTokenRequest, RefreshAccessTokenResponse>(
                new RefreshAccessTokenRequest { RefreshToken = rawToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (result.RefreshToken != rawToken).ShouldBe(rotate);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var hash = TokenService.HashToken(result.RefreshToken);
        var session = await read.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.TokenHash == hash, TestContext.Current.CancellationToken);
        session.SecondFactorRevision.ShouldBe(assured ? 2u : null);
        session.SecondFactorVerifiedAt.ShouldBe(assured ? originalTime : null);
    }

    [Fact]
    public async Task When_FactorChangesDuringRecoveryLogin_Then_StaleSessionCommitRollsBack()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var factor = TotpCredential.StartEnrollment(user.Id, "synthetic-factor", now);
            factor.Confirm(1, now);
            db.TotpCredentials.Add(factor);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using var loginScope = apiFactory.Services.CreateAsyncScope();
        var login = loginScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var staleFactor = await login.TotpCredentials.SingleAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken);
        var sessionId = Guid.NewGuid();
        login.MarkPropertyAsUpdated(staleFactor, t => t.ConfigurationRevision);
        login.Add(RefreshToken.Create(sessionId, user.Id, organization.Id, "synthetic-session-hash", 1,
            now + Duration.FromDays(30), now, staleFactor.ConfigurationRevision, now));

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var factor = await db.TotpCredentials.SingleAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken);
            factor.Disable(now);
            factor.RestartEnrollment("replacement-factor", now);
            factor.Confirm(2, now);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => login.CommitAsync(TestContext.Current.CancellationToken));
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.RefreshTokens.AnyAsync(t => t.UserId == user.Id && t.Id == sessionId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await read.TotpCredentials.SingleAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken)).ConfigurationRevision.ShouldBe(5u);
    }
}
