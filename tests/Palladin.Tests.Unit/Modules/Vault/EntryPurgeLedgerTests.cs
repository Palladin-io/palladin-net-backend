using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Purge;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EntryPurgeLedgerTests
{
    [Fact]
    public async Task When_PurgeRecordIsAppended_Then_OnlyOpaqueComplianceLockedPayloadIsDurable()
    {
        var s3 = Substitute.For<IAmazonS3>();
        PutObjectRequest? put = null;
        s3.PutObjectAsync(Arg.Do<PutObjectRequest>(request => put = request), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
        s3.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(put!.ContentBody)),
                ObjectLockMode = ObjectLockMode.Compliance,
                ObjectLockRetainUntilDate = put!.ObjectLockRetainUntilDate,
            });
        var keyProvider = Substitute.For<IEntryPurgeKeyProvider>();
        keyProvider.GetKeyAsync(3, Arg.Any<CancellationToken>()).Returns(new byte[32]);
        var ledger = new S3EntryPurgeLedger(
            s3,
            new EntryPurgeTokenGenerator(keyProvider),
            Options.Create(OptionsValue()));
        var scope = new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var purgedAt = Instant.FromUtc(2026, 7, 18, 12, 0) + Duration.FromMilliseconds(123);

        var durable = await ledger.AppendAsync(scope, purgedAt, TestContext.Current.CancellationToken);

        durable.PurgeKeyVersion.ShouldBe(3u);
        durable.PurgedAt.ShouldBe(purgedAt);
        durable.OpaqueEntryToken.Length.ShouldBe(64);
        put.ShouldNotBeNull();
        put!.IfNoneMatch.ShouldBe("*");
        put.ObjectLockMode.ShouldBe(ObjectLockMode.Compliance);
        put.ObjectLockRetainUntilDate.ShouldBe(
            (purgedAt + Duration.FromDays(120) + Duration.FromMilliseconds(877)).ToDateTimeUtc());
        put.ServerSideEncryptionMethod.ShouldBe(ServerSideEncryptionMethod.AES256);
        put.ContentBody.ShouldNotContain(scope.OrganizationId.ToString());
        put.ContentBody.ShouldNotContain(scope.VaultId.ToString());
        put.ContentBody.ShouldNotContain(scope.EntryId.ToString());
    }

    [Fact]
    public async Task When_DurablePurgeObjectIsNotComplianceLocked_Then_AppendFailsClosed()
    {
        var s3 = Substitute.For<IAmazonS3>();
        PutObjectRequest? put = null;
        s3.PutObjectAsync(Arg.Do<PutObjectRequest>(request => put = request), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
        s3.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(put!.ContentBody)),
            });
        var keyProvider = Substitute.For<IEntryPurgeKeyProvider>();
        keyProvider.GetKeyAsync(3, Arg.Any<CancellationToken>()).Returns(new byte[32]);
        var ledger = new S3EntryPurgeLedger(
            s3,
            new EntryPurgeTokenGenerator(keyProvider),
            Options.Create(OptionsValue()));

        var action = () => ledger.AppendAsync(
            new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            Instant.FromUtc(2026, 7, 18, 12, 0),
            TestContext.Current.CancellationToken);

        var exception = await action.ShouldThrowAsync<InvalidOperationException>();
        exception.Message.ShouldContain("Compliance locked");
    }

    [Fact]
    public async Task When_PurgeLedgerContainsDeleteMarker_Then_ReplayFailsClosed()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListVersionsResponse
            {
                Versions =
                [
                    new S3ObjectVersion
                    {
                        Key = "vault-entry-purge-ledger/entries/3/opaque.json",
                        VersionId = "delete-marker-version",
                        IsDeleteMarker = true,
                    },
                ],
            });
        var keyProvider = Substitute.For<IEntryPurgeKeyProvider>();
        var ledger = new S3EntryPurgeLedger(
            s3,
            new EntryPurgeTokenGenerator(keyProvider),
            Options.Create(OptionsValue()));

        var action = () => ledger.ReadAllAsync(TestContext.Current.CancellationToken);

        var exception = await action.ShouldThrowAsync<InvalidOperationException>();
        exception.Message.ShouldContain("delete marker");
        await s3.DidNotReceive().GetObjectAsync(
            Arg.Any<GetObjectRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_SsmParameterIsNotSecureString_Then_KeyLoadFailsClosed()
    {
        var ssm = Substitute.For<IAmazonSimpleSystemsManagement>();
        ssm.GetParameterAsync(Arg.Any<GetParameterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetParameterResponse
            {
                Parameter = new Parameter
                {
                    Type = ParameterType.String,
                    Value = Convert.ToBase64String(new byte[32]),
                },
            });
        var provider = new SsmEntryPurgeKeyProvider(ssm, Options.Create(OptionsValue()));

        var action = () => provider.GetKeyAsync(3, TestContext.Current.CancellationToken);

        var exception = await action.ShouldThrowAsync<InvalidOperationException>();
        exception.Message.ShouldContain("SecureString");
    }

    private static EntryPurgeLedgerOptions OptionsValue() => new()
    {
        Enabled = true,
        BucketName = "example-purge-ledger",
        Region = "eu-west-1",
        ObjectPrefix = "vault-entry-purge-ledger",
        KeyParameterPrefix = "/example/prod/vault/purge-key",
        CurrentKeyVersion = 3,
        RetentionDays = 120,
    };
}
