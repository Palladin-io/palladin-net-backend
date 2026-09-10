using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Options;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class RefreshTokenLineageConcurrencyTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_RotationCommitsDuringRevocation_Then_RetryRevokesTheNewSuccessor(bool logout)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var initial = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, Guid.NewGuid().ToString("N"));
        var other = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, Guid.NewGuid().ToString("N"));
        var successorId = Guid.NewGuid();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var interceptor = new RotateBeforeFirstCommit(async () =>
        {
            await using var concurrent = apiFactory.Services.CreateAsyncScope();
            var database = concurrent.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var current = await database.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.Id == initial.Id, Ct);
            current.Revoke(Now, successorId);
            database.RefreshTokens.Add(RefreshToken.Create(successorId, user.Id, user.OrganizationId,
                TokenService.HashToken(Guid.NewGuid().ToString("N")), 1, Now + Duration.FromDays(365), Now,
                sessionId: current.SessionId));
            await database.SaveChangesAsync(Ct);
        });
        var databaseOptions = new DbContextOptionsBuilder<IdentityDbWriteContext>(
            scope.ServiceProvider.GetRequiredService<DbContextOptions<IdentityDbWriteContext>>()).AddInterceptors(interceptor).Options;
        await using var database = new IdentityDbWriteContext(databaseOptions);
        var context = new IdentityDomainWriteContext(database, []);
        var revoker = new RefreshTokenLineageRevoker(context, Options.Create(new JwtOptions { RefreshTokenConcurrencyRetryLimit = 2 }));

        // When
        var result = logout ? await revoker.RevokeForLogoutAsync(user.Id, initial.Id, Now, Ct)
            : await revoker.RevokeAfterReplayAsync(user.Id, initial.Id, Now, Ct);

        // Then
        result.ShouldBeTrue();
        interceptor.Attempts.ShouldBe(2);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.Id == successorId, Ct)).IsRevoked.ShouldBeTrue();
        (await read.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.Id == initial.Id, Ct)).ReplacedByTokenId.ShouldBe(successorId);
        (await read.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.Id == other.Id, Ct)).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task When_LogoutPresentsAnAlreadyRotatedToken_Then_OnlyItsCurrentSuccessorIsRevoked()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var successor = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, Guid.NewGuid().ToString("N"));
        var oldRaw = Guid.NewGuid().ToString("N");
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, oldRaw, revokedAt: Now, replacedByTokenId: successor.Id);
        var other = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, Guid.NewGuid().ToString("N"));

        // When
        var response = await apiFactory.CreateAuthenticatedClient(user)
            .POSTAsync<LogoutEndpoint, LogoutRequest>(new() { RefreshToken = oldRaw });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.Id == successor.Id, Ct)).IsRevoked.ShouldBeTrue();
        (await read.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.Id == other.Id, Ct)).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task When_CorruptLineagePointsAtAnotherAccount_Then_RevocationCannotCrossTheAccountBoundary()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (other, _, _) = await apiFactory.Services.SeedUserAsync();
        var foreign = await apiFactory.Services.SeedRefreshTokenAsync(other.Id, Guid.NewGuid().ToString("N"));
        var raw = Guid.NewGuid().ToString("N");
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw, revokedAt: Now, replacedByTokenId: foreign.Id);

        // When
        var (response, _) = await apiFactory.CreateClient().POSTAsync<RefreshAccessTokenEndpoint,
            RefreshAccessTokenRequest, RefreshAccessTokenResponse>(new() { RefreshToken = raw });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().RefreshTokens
            .SingleAsync(t => t.UserId == other.Id && t.Id == foreign.Id, Ct)).IsRevoked.ShouldBeFalse();
    }

    private Instant Now => apiFactory.FakeClock.GetCurrentInstant();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RotateBeforeFirstCommit(Func<Task> rotate) : SaveChangesInterceptor
    {
        internal int Attempts { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++Attempts == 1) { await rotate(); }
            return result;
        }
    }
}
