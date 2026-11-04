using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Cryptography;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Crypto;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class SecretKeyTransferTests(ApiFactory apiFactory) : TestBase
{
    private static readonly JsonSerializerOptions Json = Palladin.Core.Json.PalladinJsonSerializationSettings.DefaultOptions!;

    [Fact]
    public async Task When_RecipientShowsQr_Then_FirstSenderBindsAndRecipientClaimsCiphertextExactlyOnce()
    {
        // Given
        var recipientKey = XWingTestKeys.CreateKeyPair();
        var anonymous = apiFactory.CreateClient();
        var started = await StartAsRecipientAsync(anonymous, recipientKey.PublicKey);
        var sender = await SeedAccountAsync();
        var otherAccount = await SeedAccountAsync();

        // When
        var pending = await ClaimAsync(anonymous, started.TransferId, started.PollToken);
        var senderView = await sender.Client.GetFromJsonAsync<GetSecretKeyTransferResponse>(
            $"api/account/secret-key-transfers/{started.TransferId}", Json);
        var foreignView = await otherAccount.Client.GetAsync($"api/account/secret-key-transfers/{started.TransferId}");
        var envelope = Envelope();
        var sealedResponse = await sender.Client.PostAsJsonAsync(
            $"api/account/secret-key-transfers/{started.TransferId}/envelope", envelope);
        var claimed = await ClaimAsync(anonymous, started.TransferId, started.PollToken);
        var replay = await ClaimAsync(anonymous, started.TransferId, started.PollToken);

        // Then
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        senderView!.AccountId.ShouldBe(sender.User.Id);
        senderView.Challenge.ShouldBe(started.Challenge);
        senderView.RecipientPublicKey.ShouldBe(recipientKey.PublicKey);
        senderView.State.ShouldBe(SecretKeyTransferStateContract.Bound);
        foreignView.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        sealedResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        claimed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await claimed.Content.ReadFromJsonAsync<ClaimSecretKeyTransferResponse>(Json))!;
        body.AccountId.ShouldBe(sender.User.Id);
        body.KemCiphertext.ShouldBe(envelope.KemCiphertext);
        body.Ciphertext.ShouldBe(envelope.Ciphertext);
        replay.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await replay.Content.ReadAsStringAsync()).ShouldContain("pairing-consumed");
        var stored = await ReadAsync(started.TransferId);
        stored.KemCiphertext.ShouldBeNull();
        stored.SealedSecret.ShouldBeNull();
        stored.PollTokenHash.ShouldBe(SHA256.HashData(started.PollToken));
    }

    [Fact]
    public async Task When_SenderShowsQr_Then_OnlyTheMatchingChallengeAttachesTheRecipientOnce()
    {
        // Given
        var sender = await SeedAccountAsync();
        var anonymous = apiFactory.CreateClient();
        var created = (await (await sender.Client.PostAsync("api/account/secret-key-transfers", null))
            .Content.ReadFromJsonAsync<StartSecretKeyTransferAsSenderResponse>(Json))!;
        var recipientKey = XWingTestKeys.CreateKeyPair();

        // When
        var wrongChallenge = await AttachAsync(anonymous, created.TransferId, RandomNumberGenerator.GetBytes(32), recipientKey.PublicKey);
        var attached = await AttachAsync(anonymous, created.TransferId, created.Challenge, recipientKey.PublicKey);
        var secondAttach = await AttachAsync(anonymous, created.TransferId, created.Challenge, recipientKey.PublicKey);
        var pollToken = (await attached.Content.ReadFromJsonAsync<AttachSecretKeyTransferRecipientResponse>(Json))!.PollToken;
        var envelope = Envelope();
        (await sender.Client.PostAsJsonAsync($"api/account/secret-key-transfers/{created.TransferId}/envelope", envelope))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var claimed = await ClaimAsync(anonymous, created.TransferId, pollToken);

        // Then
        wrongChallenge.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        attached.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondAttach.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        claimed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await claimed.Content.ReadFromJsonAsync<ClaimSecretKeyTransferResponse>(Json))!.AccountId.ShouldBe(sender.User.Id);
    }

    [Fact]
    public async Task When_TransferExpires_Then_ClaimAndEnvelopeAreRejected()
    {
        // Given
        var anonymous = apiFactory.CreateClient();
        var started = await StartAsRecipientAsync(anonymous, XWingTestKeys.CreateKeyPair().PublicKey);
        var sender = await SeedAccountAsync();
        (await sender.Client.GetAsync($"api/account/secret-key-transfers/{started.TransferId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var originalTime = apiFactory.FakeClock.GetCurrentInstant();
        apiFactory.FakeClock.Advance(Duration.FromSeconds(301));

        // When
        HttpResponseMessage sealedResponse;
        HttpResponseMessage claim;
        try
        {
            sealedResponse = await sender.Client.PostAsJsonAsync(
                $"api/account/secret-key-transfers/{started.TransferId}/envelope", Envelope());
            claim = await ClaimAsync(anonymous, started.TransferId, started.PollToken);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalTime);
        }

        // Then
        sealedResponse.StatusCode.ShouldBe(HttpStatusCode.Gone);
        claim.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await claim.Content.ReadAsStringAsync()).ShouldContain("pairing-expired");
    }

    [Fact]
    public async Task When_PollTokenOrLengthsAreWrong_Then_NothingIsReleasedOrStored()
    {
        // Given
        var anonymous = apiFactory.CreateClient();
        var started = await StartAsRecipientAsync(anonymous, XWingTestKeys.CreateKeyPair().PublicKey);
        var sender = await SeedAccountAsync();
        await sender.Client.GetAsync($"api/account/secret-key-transfers/{started.TransferId}");

        // When
        var shortKey = await anonymous.PostAsJsonAsync("api/auth/secret-key-transfers",
            new { recipientPublicKey = WebEncoders.Base64UrlEncode(new byte[32]) });
        var shortEnvelope = await sender.Client.PostAsJsonAsync(
            $"api/account/secret-key-transfers/{started.TransferId}/envelope",
            Envelope() with { KemCiphertext = new byte[XWingKem.CiphertextBytes - 1] });
        await sender.Client.PostAsJsonAsync($"api/account/secret-key-transfers/{started.TransferId}/envelope", Envelope());
        var wrongToken = await ClaimAsync(anonymous, started.TransferId, RandomNumberGenerator.GetBytes(32));

        // Then
        shortKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        shortEnvelope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        wrongToken.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadAsync(started.TransferId)).State.ShouldBe(SecretKeyTransferState.Sealed);
    }

    [Fact]
    public async Task When_EitherPartyCancels_Then_TransferIsDeletedOnlyByAnAuthorizedParty()
    {
        // Given
        var anonymous = apiFactory.CreateClient();
        var first = await StartAsRecipientAsync(anonymous, XWingTestKeys.CreateKeyPair().PublicKey);
        var second = await StartAsRecipientAsync(anonymous, XWingTestKeys.CreateKeyPair().PublicKey);
        var sender = await SeedAccountAsync();
        var otherAccount = await SeedAccountAsync();
        await sender.Client.GetAsync($"api/account/secret-key-transfers/{second.TransferId}");

        // When
        var wrongRecipient = await CancelAsRecipientAsync(anonymous, first.TransferId, RandomNumberGenerator.GetBytes(32));
        var recipientCancel = await CancelAsRecipientAsync(anonymous, first.TransferId, first.PollToken);
        var foreignCancel = await otherAccount.Client.DeleteAsync($"api/account/secret-key-transfers/{second.TransferId}");
        var senderCancel = await sender.Client.DeleteAsync($"api/account/secret-key-transfers/{second.TransferId}");

        // Then
        wrongRecipient.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        recipientCancel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        foreignCancel.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        senderCancel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().SecretKeyTransfers
            .AnyAsync(x => x.Id == first.TransferId || x.Id == second.TransferId)).ShouldBeFalse();
    }

    private async Task<(Palladin.Module.Identity.Domain.User User, HttpClient Client)> SeedAccountAsync()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(UserFaker.CreateOnboarded());
        return (user, apiFactory.CreateAuthenticatedClient(user));
    }

    private static async Task<StartSecretKeyTransferAsRecipientResponse> StartAsRecipientAsync(HttpClient client, byte[] publicKey)
    {
        var response = await client.PostAsJsonAsync("api/auth/secret-key-transfers",
            new { recipientPublicKey = WebEncoders.Base64UrlEncode(publicKey) });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<StartSecretKeyTransferAsRecipientResponse>(Json))!;
    }

    private static Task<HttpResponseMessage> AttachAsync(HttpClient client, Guid transferId, byte[] challenge, byte[] publicKey) =>
        client.PutAsJsonAsync($"api/auth/secret-key-transfers/{transferId}/recipient", new
        {
            challenge = WebEncoders.Base64UrlEncode(challenge),
            recipientPublicKey = WebEncoders.Base64UrlEncode(publicKey),
        });

    private static Task<HttpResponseMessage> ClaimAsync(HttpClient client, Guid transferId, byte[] pollToken) =>
        client.PostAsJsonAsync($"api/auth/secret-key-transfers/{transferId}/claim",
            new { pollToken = WebEncoders.Base64UrlEncode(pollToken) });

    private static Task<HttpResponseMessage> CancelAsRecipientAsync(HttpClient client, Guid transferId, byte[] pollToken) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"api/auth/secret-key-transfers/{transferId}")
        {
            Content = JsonContent.Create(new { pollToken = WebEncoders.Base64UrlEncode(pollToken) }),
        });

    private static SealSecretKeyTransferRequest Envelope() => new()
    {
        KemCiphertext = RandomNumberGenerator.GetBytes(XWingKem.CiphertextBytes),
        Ciphertext = RandomNumberGenerator.GetBytes(48),
    };

    private async Task<SecretKeyTransfer> ReadAsync(Guid transferId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().SecretKeyTransfers
            .SingleAsync(x => x.Id == transferId);
    }
}
